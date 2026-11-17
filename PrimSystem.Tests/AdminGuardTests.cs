using PrimSystem.Data;
using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class AdminGuardTests
    {
        [Fact]
        public void Aktif_Admin_Gecer()
        {
            Assert.True(AdminGuard.IsAdmin(RoleId.Admin, isActive: true));
        }

        [Fact]
        public void Pasiflestirilmis_Admin_Gecmez()
        {
            Assert.False(AdminGuard.IsAdmin(RoleId.Admin, isActive: false));
        }

        [Theory]
        [InlineData(RoleId.Yonetici)]
        [InlineData(RoleId.BM)]
        [InlineData(RoleId.ST)]
        [InlineData(0)]
        public void Admin_Olmayan_Rol_Gecmez(int rol)
        {
            // Rolü düşürülmüş eski admin: oturumdaki kopya Admin kalsa da veritabanındaki rol bakılır.
            Assert.False(AdminGuard.IsAdmin(rol, isActive: true));
        }
    }
}
