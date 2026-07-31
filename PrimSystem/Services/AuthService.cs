using Microsoft.EntityFrameworkCore;
using PrimSystem.Data;
using System.Security.Cryptography;

namespace PrimSystem.Services
{
    public class AuthService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly LoginThrottle _throttle;
        private readonly AuditService _audit;
        private readonly ILogger<AuthService> _logger;

        public AuthService(IDbContextFactory<AppDbContext> factory, LoginThrottle throttle,
            AuditService audit, ILogger<AuthService> logger)
        {
            _factory = factory;
            _throttle = throttle;
            _audit = audit;
            _logger = logger;
        }

        // Servis katmanı yetki kontrolü — UI guard'larına ek savunma hattı
        private async Task EnsureAdminAsync(int actorUserId)
        {
            using var db = _factory.CreateDbContext();
            var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == actorUserId);
            if (actor == null || actor.RoleId != RoleId.Admin)
                throw new UnauthorizedAccessException("Bu işlem için yetkiniz yok.");
        }

        private static string HashToken(string token) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        public async Task<User?> LoginAsync(string username, string password, string? ipAddress = null)
        {
            // Brute-force koruması — kilitliyse hemen reddet
            if (_throttle.GetLockRemaining(username) is { } remaining)
            {
                // AuditLogs.UserId FK'lı; kilit kontrolü kullanıcı çözülmeden önce olduğu için
                // buraya audit yazılamaz. Güvenlik izi Serilog'a düşer.
                _logger.LogWarning("Giriş reddedildi (kilitli): kullanıcı={Username} ip={Ip} kalan={Kalan}dk",
                    username, ipAddress ?? "-", Math.Ceiling(remaining.TotalMinutes));
                throw new LoginLockedException(
                    $"Çok fazla hatalı deneme. Lütfen {Math.Ceiling(remaining.TotalMinutes)} dakika sonra tekrar deneyin.");
            }

            using var db = _factory.CreateDbContext();
            var user = await db.Users
                .Include(u => u.Role)
                .Include(u => u.Region)
                .FirstOrDefaultAsync(u => (u.Username == username || u.Email == username) && u.IsActive && !u.IsDeleted);

            // Bilinmeyen kullanıcı adı: AuditLogs.UserId FK'lı olduğundan denetim kaydı YAZILAMAZ
            // (sahte bir UserId uydurmak yerine Serilog'a düşülüyor). Kullanıcı-adı sayımını
            // (enumeration) kolaylaştırmamak için çağırana dönen sonuç yanlış-parola ile aynı.
            if (user == null)
            {
                _throttle.RegisterFailure(username);
                _logger.LogWarning("Başarısız giriş (kullanıcı yok): kullanıcı={Username} ip={Ip}",
                    username, ipAddress ?? "-");
                return null;
            }

            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                _throttle.RegisterFailure(username);
                // Hedefli deneme — kullanıcı gerçek, parola yanlış. Güvenlik incelemesinde en
                // değerli olay bu ve UserId elimizde olduğu için audit'e yazılabiliyor.
                await _audit.LogAsync(user.UserId, "LoginFailed", "User", user.UserId,
                    newValue: "Hatalı parola", ipAddress: ipAddress);
                return null;
            }

            _throttle.Reset(username);

            user.LastLoginAt = DateTime.UtcNow;

            // Önceki aktif oturumu kapat
            var prev = await db.UserSessions.FirstOrDefaultAsync(s => s.UserId == user.UserId && s.IsActive);
            if (prev != null) { prev.IsActive = false; prev.LogoutAt = DateTime.UtcNow; }

            // Yeni oturum oluştur
            var session = new UserSession
            {
                UserId = user.UserId,
                LoginAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                IpAddress = ipAddress,
                IsActive = true
            };
            db.UserSessions.Add(session);
            await db.SaveChangesAsync();

            user.ActiveSessionId = session.SessionId;
            await db.SaveChangesAsync();

            await _audit.LogAsync(user.UserId, "Login", "User", user.UserId,
                newValue: $"Oturum #{session.SessionId}", ipAddress: ipAddress);

            return user;
        }

        public static string GenerateUsername(string fullName)
        {
            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            var first = parts[0];
            var last = parts.Length > 1 ? parts[^1] : "";
            var lastPrefix = last.Length >= 2 ? last[..2] : last;
            return first + lastPrefix;
        }

        // Karışıklık yaratabilen karakterler (0/O, 1/I/l) çıkarıldı
        private const string PasswordChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789abcdefghijkmnpqrstuvwxyz";

        public static string GenerateDefaultPassword(string fullName)
        {
            // fullName parametresi backward-compat için duruyor ama artık kullanılmıyor.
            // 10 karakter cryptographically secure random.
            const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
            const string digits = "23456789";
            var bytes = RandomNumberGenerator.GetBytes(14);
            var chars = new char[10];
            for (int i = 0; i < 10; i++)
                chars[i] = PasswordChars[bytes[i] % PasswordChars.Length];
            // ValidatePasswordStrength garantisi: en az bir harf ve bir rakam içermeli
            chars[bytes[10] % 5] = letters[bytes[11] % letters.Length];
            chars[5 + bytes[12] % 5] = digits[bytes[13] % digits.Length];
            return new string(chars);
        }

        // Server-side parola politikası — UI doğrulamasına ek savunma.
        // En az 8 karakter, en az bir harf ve bir rakam içermeli.
        public static void ValidatePasswordStrength(string password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
                throw new ArgumentException("Şifre en az 8 karakter olmalıdır.");
            if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
                throw new ArgumentException("Şifre en az bir harf ve bir rakam içermelidir.");
        }

        public async Task<bool> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
        {
            ValidatePasswordStrength(newPassword);
            using var db = _factory.CreateDbContext();
            var user = await db.Users.FindAsync(userId);
            if (user == null) return false;
            if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash)) return false;
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            user.MustChangePassword = false;
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "PasswordChange", "User", userId);
            return true;
        }

        public async Task<int> BulkResetPasswordsAsync(int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            using var db = _factory.CreateDbContext();
            var users = await db.Users.Where(u => u.IsActive && !u.IsDeleted).ToListAsync();
            foreach (var u in users)
            {
                var pwd = GenerateDefaultPassword(u.FullName);
                u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(pwd);
                u.MustChangePassword = true;
                u.ResetToken = null;
                u.ResetTokenExpiry = null;
            }
            await db.SaveChangesAsync();
            await _audit.LogAsync(actorUserId, "PasswordBulkReset", "User", null,
                newValue: $"{users.Count} kullanıcının parolası sıfırlandı");
            return users.Count;
        }

        public async Task<string> AdminResetPasswordAsync(int userId, int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            using var db = _factory.CreateDbContext();
            var user = await db.Users.FindAsync(userId);
            if (user == null) return "";
            var pwd = GenerateDefaultPassword(user.FullName);
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(pwd);
            user.MustChangePassword = true;
            user.ResetToken = null;
            user.ResetTokenExpiry = null;
            await db.SaveChangesAsync();
            await _audit.LogAsync(actorUserId, "PasswordAdminReset", "User", userId);
            return pwd;
        }

        public async Task<string?> ForgotPasswordAsync(string email)
        {
            // E-posta bazlı rate limit — token üretim spam'ini engeller
            if (_throttle.GetLockRemaining("fp:" + email) != null) return null;
            _throttle.RegisterFailure("fp:" + email);

            using var db = _factory.CreateDbContext();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.IsActive && !u.IsDeleted);
            if (user == null) return null;
            // Ham token e-posta linkine gider; DB'de yalnızca SHA256 hash'i tutulur
            var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            user.ResetToken = HashToken(rawToken);
            user.ResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
            return rawToken;
        }

        public async Task<bool> ResetPasswordAsync(string token, string newPassword)
        {
            ValidatePasswordStrength(newPassword);
            var tokenHash = HashToken(token);
            using var db = _factory.CreateDbContext();
            var user = await db.Users.FirstOrDefaultAsync(u =>
                u.ResetToken == tokenHash && u.ResetTokenExpiry > DateTime.UtcNow);
            if (user == null) return false;
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            user.MustChangePassword = false;
            user.ResetToken = null;
            user.ResetTokenExpiry = null;
            await db.SaveChangesAsync();
            await _audit.LogAsync(user.UserId, "PasswordTokenReset", "User", user.UserId);
            return true;
        }

        public async Task<User?> GetUserByIdAsync(int userId)
        {
            using var db = _factory.CreateDbContext();
            return await db.Users
                .Include(u => u.Role)
                .Include(u => u.Region)
                .FirstOrDefaultAsync(u => u.UserId == userId);
        }
    }
}
