using Microsoft.EntityFrameworkCore;
using PrimSystem.Data;

namespace PrimSystem.Services
{
    public class NotificationService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;

        public NotificationService(IDbContextFactory<AppDbContext> factory) { _factory = factory; }

        // Toplu gönderim yetkisi: sistem bildirimi (senderId yok) serbest;
        // kullanıcı gönderimi Admin/Yonetici/BM rollerine sınırlı (UI guard'ına ek savunma).
        private async Task EnsureCanSendBulkAsync(int? senderId)
        {
            if (senderId == null) return; // sistem kaynaklı (örn. yayın bildirimi)
            using var db = _factory.CreateDbContext();
            var sender = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == senderId.Value);
            if (sender == null || sender.RoleId is not (RoleId.Admin or RoleId.Yonetici or RoleId.BM))
                throw new UnauthorizedAccessException("Toplu bildirim göndermek için yetkiniz yok.");
        }

        public async Task<int> GetUnreadCountAsync(int userId)
        {
            using var db = _factory.CreateDbContext();
            return await db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
        }

        public async Task<List<Notification>> GetForUserAsync(int userId, int take = 50)
        {
            using var db = _factory.CreateDbContext();
            return await db.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .Take(take)
                .ToListAsync();
        }

        public async Task<List<Notification>> GetPreviewAsync(int userId, int take = 6)
        {
            using var db = _factory.CreateDbContext();
            return await db.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .Take(take)
                .ToListAsync();
        }

        public async Task MarkReadAsync(int notifId, int userId)
        {
            using var db = _factory.CreateDbContext();
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.NotifId == notifId && x.UserId == userId);
            if (n != null && !n.IsRead) { n.IsRead = true; await db.SaveChangesAsync(); }
        }

        public async Task MarkAllReadAsync(int userId)
        {
            using var db = _factory.CreateDbContext();
            var unread = await db.Notifications.Where(n => n.UserId == userId && !n.IsRead).ToListAsync();
            if (!unread.Any()) return;
            unread.ForEach(n => n.IsRead = true);
            await db.SaveChangesAsync();
        }

        public async Task SendAsync(int toUserId, string title, string? message, string type,
            int? senderId = null, string senderType = "User")
        {
            using var db = _factory.CreateDbContext();
            db.Notifications.Add(new Notification
            {
                UserId = toUserId, Title = title, Message = message, Type = type,
                SenderType = senderType, SenderId = senderId, IsRead = false, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        public async Task SendBulkAsync(IEnumerable<int> userIds, string title, string? message,
            string type, int? senderId = null, string senderType = "User")
        {
            await EnsureCanSendBulkAsync(senderId);
            using var db = _factory.CreateDbContext();
            var now = DateTime.UtcNow;
            var list = userIds.Distinct().Select(uid => new Notification
            {
                UserId = uid, Title = title, Message = message, Type = type,
                SenderType = senderType, SenderId = senderId, IsRead = false, CreatedAt = now
            }).ToList();
            if (list.Any()) { db.Notifications.AddRange(list); await db.SaveChangesAsync(); }
        }

        public async Task SendToRoleAsync(int roleId, string title, string? message, string type, int? senderId = null)
        {
            await EnsureCanSendBulkAsync(senderId);
            using var db = _factory.CreateDbContext();
            var ids = await db.Users
                .Where(u => u.RoleId == roleId && u.IsActive && !u.IsDeleted)
                .Select(u => u.UserId).ToListAsync();
            await SendBulkAsync(ids, title, message, type, senderId);
        }

        public async Task SendToRegionAsync(int regionId, string title, string? message, string type, int? senderId = null)
        {
            await EnsureCanSendBulkAsync(senderId);
            using var db = _factory.CreateDbContext();
            var ids = await db.Users
                .Where(u => u.RegionId == regionId && u.RoleId == RoleId.ST && u.IsActive && !u.IsDeleted)
                .Select(u => u.UserId).ToListAsync();
            await SendBulkAsync(ids, title, message, type, senderId);
        }

        public async Task SendToManagerTeamAsync(int managerId, string title, string? message, string type, int? senderId = null)
        {
            await EnsureCanSendBulkAsync(senderId);
            using var db = _factory.CreateDbContext();
            var ids = await db.Users
                .Where(u => u.ManagerId == managerId && u.RoleId == RoleId.ST && u.IsActive && !u.IsDeleted)
                .Select(u => u.UserId).ToListAsync();
            await SendBulkAsync(ids, title, message, type, senderId);
        }
    }
}
