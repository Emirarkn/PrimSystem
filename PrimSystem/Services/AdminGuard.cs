using Microsoft.EntityFrameworkCore;
using PrimSystem.Data;

namespace PrimSystem.Services
{
    /// <summary>
    /// Durum değiştiren sayfa eylemlerinden önce çağrılır: aktörün rolünü oturumdaki kopyadan değil
    /// veritabanından taze okur. Oturumdaki kullanıcı nesnesi rol değişimini veya pasifleştirmeyi
    /// yansıtmıyor (SEC-002); yetkisi alınmış bir admin açık sekmeden KPI ve kullanıcı yazabiliyordu.
    /// </summary>
    public class AdminGuard
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        public AdminGuard(IDbContextFactory<AppDbContext> factory) { _factory = factory; }

        /// <summary>Yalnız aktif Admin geçer.</summary>
        public static bool IsAdmin(int roleId, bool isActive) => isActive && roleId == RoleId.Admin;

        public async Task EnsureAdminAsync(int actorUserId)
        {
            using var db = _factory.CreateDbContext();
            var actor = await db.Users.AsNoTracking()
                .Where(u => u.UserId == actorUserId)
                .Select(u => new { u.RoleId, u.IsActive })
                .FirstOrDefaultAsync();
            if (actor == null || !IsAdmin(actor.RoleId, actor.IsActive))
                throw new UnauthorizedAccessException("Bu işlem için yetkiniz yok.");
        }
    }
}
