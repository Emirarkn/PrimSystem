using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.EntityFrameworkCore;
using PrimSystem.Data;

namespace PrimSystem.Services
{
    public class SessionState : IDisposable
    {
        private readonly AuthService _auth;
        private readonly SessionStore _store;
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly AuditService _audit;
        private readonly ILogger<SessionState> _logger;
        private string? _token;
        private System.Threading.Timer? _heartbeat;

        public SessionState(AuthService auth, SessionStore store, IDbContextFactory<AppDbContext> factory, AuditService audit, ILogger<SessionState> logger)
        {
            _auth = auth;
            _store = store;
            _factory = factory;
            _audit = audit;
            _logger = logger;
        }

        public User? CurrentUser => _token != null ? _store.Get(_token) : null;
        public bool IsLoggedIn => CurrentUser != null;

        // İlk oturum restore denemesi tamamlandı mı? Layout, restore denenmeden
        // LoginRequired göstermemek için bu bayrağı bekler (yenileme flash'ını önler).
        public bool RestoreAttempted { get; private set; }
        public string RoleName => CurrentUser?.Role?.Name ?? "";
        public bool IsAdmin => RoleName == "Admin";
        public bool IsYonetici => RoleName == "Yonetici";
        public bool IsBM => RoleName == "BM";
        public bool IsST => RoleName == "ST";
        public bool CanManage => IsAdmin;
        public bool CanViewAll => IsAdmin || IsYonetici;
        public bool CanSendNotification => IsAdmin || IsYonetici || IsBM;

        public int? ImpersonatorId { get; private set; }
        public bool IsImpersonating => ImpersonatorId.HasValue;

        public event Action? OnChange;

        public async Task LoginAsync(User user, ProtectedSessionStorage storage)
        {
            _token = Guid.NewGuid().ToString("N");
            _store.Set(_token, user);
            await storage.SetAsync("session_token", _token);
            StartHeartbeat();
            OnChange?.Invoke();
        }

        public async Task LogoutAsync(ProtectedSessionStorage storage)
        {
            await CloseSessionAsync();
            if (_token != null) _store.Remove(_token);
            _token = null;
            await storage.DeleteAsync("session_token");
            OnChange?.Invoke();
        }

        /// <summary>
        /// Restores session from browser storage. Checks the in-memory store first
        /// (no DB call needed when the server is still running); falls back to DB lookup.
        /// Call from OnAfterRenderAsync(firstRender).
        /// </summary>
        public async Task RestoreSessionAsync(ProtectedSessionStorage storage)
        {
            if (CurrentUser != null) { RestoreAttempted = true; return; }

            try
            {
                var tokenResult = await storage.GetAsync<string>("session_token");
                if (!tokenResult.Success || string.IsNullOrEmpty(tokenResult.Value)) { RestoreAttempted = true; return; }

                _token = tokenResult.Value;

                // Fast path: already in Singleton store (server hasn't restarted)
                if (_store.Get(_token) != null) { RestoreAttempted = true; OnChange?.Invoke(); return; }

                // Slow path: server restarted → restore from DB via persisted userId
                var idResult = await storage.GetAsync<int>("userId");
                if (idResult.Success && idResult.Value > 0)
                {
                    var user = await _auth.GetUserByIdAsync(idResult.Value);
                    if (user != null && user.IsActive && !user.IsDeleted)
                    {
                        _store.Set(_token, user);
                        OnChange?.Invoke();
                    }
                }

                var impResult = await storage.GetAsync<int>("impersonator_id");
                if (impResult.Success && impResult.Value > 0)
                    ImpersonatorId = impResult.Value;

                RestoreAttempted = true;
            }
            catch (InvalidOperationException)
            {
                /* JS interop henüz hazır değil (ilk SSR render) — RestoreAttempted false kalır,
                   böylece interaktif circuit'te OnAfterRenderAsync tekrar dener. */
            }
        }

        /// <summary>
        /// Persists userId for the server-restart fallback path.
        /// Call after LoginAsync so both token and userId survive restart.
        /// </summary>
        public async Task PersistSessionAsync(ProtectedSessionStorage storage)
        {
            if (CurrentUser != null)
                await storage.SetAsync("userId", CurrentUser.UserId);
        }

        public async Task ImpersonateAsync(User targetUser, ProtectedSessionStorage storage)
        {
            if (!IsAdmin)
                throw new UnauthorizedAccessException("Bu işlem için yetkiniz yok.");
            if (targetUser.RoleId == RoleId.Admin)
                throw new UnauthorizedAccessException("Başka bir yönetici hesabına bürünülemez.");
            var adminId = CurrentUser?.UserId;
            ImpersonatorId = adminId;
            await storage.SetAsync("impersonator_id", ImpersonatorId!.Value);
            _store.Set(_token!, targetUser);
            await storage.SetAsync("userId", targetUser.UserId);
            if (adminId.HasValue)
                await _audit.LogAsync(adminId.Value, "ImpersonateStart", "User", targetUser.UserId,
                    newValue: $"{targetUser.FullName} ({targetUser.Username}) kimliğine büründü");
            OnChange?.Invoke();
        }

        public async Task StopImpersonatingAsync(ProtectedSessionStorage storage)
        {
            if (!ImpersonatorId.HasValue) return;
            var targetId = CurrentUser?.UserId;
            var admin = await _auth.GetUserByIdAsync(ImpersonatorId.Value);
            if (admin != null)
            {
                _store.Set(_token!, admin);
                await storage.SetAsync("userId", admin.UserId);
            }
            await _audit.LogAsync(ImpersonatorId.Value, "ImpersonateStop", "User", targetId,
                newValue: "Kimliğe bürünme sonlandırıldı");
            ImpersonatorId = null;
            await storage.DeleteAsync("impersonator_id");
            OnChange?.Invoke();
        }

        public async Task ClearSessionAsync(ProtectedSessionStorage storage)
        {
            await storage.DeleteAsync("session_token");
            await storage.DeleteAsync("userId");
            await storage.DeleteAsync("impersonator_id");
        }

        // ── Oturum Takip Yardımcıları ─────────────────────────────────────────

        private void StartHeartbeat()
        {
            _heartbeat?.Dispose();
            _heartbeat = new System.Threading.Timer(
                async _ => await UpdateLastSeenAsync(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        private async Task UpdateLastSeenAsync()
        {
            var sid = CurrentUser?.ActiveSessionId;
            if (sid == null) return;
            try
            {
                using var db = _factory.CreateDbContext();
                var s = await db.UserSessions.FindAsync(sid.Value);
                if (s != null && s.IsActive) { s.LastSeenAt = DateTime.UtcNow; await db.SaveChangesAsync(); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Oturum LastSeen güncellenemedi (SessionId={SessionId})", sid);
            }
        }

        private async Task CloseSessionAsync()
        {
            var sid = CurrentUser?.ActiveSessionId;
            if (sid == null) return;
            try
            {
                using var db = _factory.CreateDbContext();
                var s = await db.UserSessions.FindAsync(sid.Value);
                if (s != null) { s.LogoutAt = DateTime.UtcNow; s.IsActive = false; await db.SaveChangesAsync(); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Oturum kapatılamadı (SessionId={SessionId})", sid);
            }
        }

        // ── Yardımcı: Force-unwrap yerine guvenli erişim ─────────────────────
        public User RequireUser()
        {
            if (CurrentUser is null)
                throw new UnauthorizedAccessException("Oturum süresi doldu, lütfen tekrar giriş yapın.");
            return CurrentUser;
        }

        public int RequireUserId() => RequireUser().UserId;

        public void Dispose()
        {
            _heartbeat?.Dispose();
        }

        // Oturum geçmişini döndür (admin için)
        public async Task<List<UserSession>> GetSessionHistoryAsync(int userId, int limit = 10)
        {
            using var db = _factory.CreateDbContext();
            return await db.UserSessions
                .Where(s => s.UserId == userId)
                .OrderByDescending(s => s.LoginAt)
                .Take(limit)
                .ToListAsync();
        }
    }
}
