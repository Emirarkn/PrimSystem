using Microsoft.EntityFrameworkCore;
using PrimSystem.Data;

namespace PrimSystem.Services
{
    public class AuditService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly ILogger<AuditService> _logger;

        public AuditService(IDbContextFactory<AppDbContext> factory, ILogger<AuditService> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        /// <summary>
        /// Denetim kaydı yazar. Hata YUTULUR (loglanır) — audit yazımı asıl işlemi düşürmemeli.
        /// </summary>
        /// <param name="ipAddress">
        /// İstemci IP'si. Blazor Server'da <c>IHttpContextAccessor</c> yalnız ilk sayfa isteğinde
        /// geçerlidir; SignalR circuit içindeki etkileşimlerde HttpContext yoktur. Bu yüzden IP
        /// otomatik türetilmez, çağıran taraf elinde varsa AÇIKÇA geçer (bkz. AuthService.LoginAsync).
        /// Geçilmezse alan null kalır — yanıltıcı bir değer üretmektense boş bırakmak yeğdir.
        /// </param>
        public async Task LogAsync(int userId, string action, string entityType, int? entityId = null,
            string? oldValue = null, string? newValue = null, string? ipAddress = null)
        {
            try
            {
                using var db = _factory.CreateDbContext();
                db.AuditLogs.Add(new AuditLog
                {
                    UserId     = userId,
                    Action     = action,
                    EntityType = entityType,
                    EntityId   = entityId,
                    OldValue   = oldValue,
                    NewValue   = newValue,
                    IpAddress  = ipAddress,
                    CreatedAt  = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit log yazılamadı (Action={Action}, EntityType={EntityType})", action, entityType);
            }
        }
    }
}
