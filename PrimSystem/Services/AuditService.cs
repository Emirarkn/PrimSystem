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

        public async Task LogAsync(int userId, string action, string entityType, int? entityId = null,
            string? oldValue = null, string? newValue = null)
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
