using PrimSystem.Data;
using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class ImpersonationRestoreTests
    {
        private static User Make(int role, bool active = true, bool deleted = false) =>
            new User { RoleId = role, IsActive = active, IsDeleted = deleted };

        [Fact]
        public void Aktif_Admin_Taklit_Oturumunu_Geri_Yukleyebilir()
        {
            Assert.True(SessionState.CanRestoreImpersonator(Make(RoleId.Admin)));
        }

        [Fact]
        public void Bulunamayan_Kullanici_Geri_Yuklenmez()
        {
            Assert.False(SessionState.CanRestoreImpersonator(null));
        }

        [Fact]
        public void Pasif_Veya_Silinmis_Admin_Geri_Yuklenmez()
        {
            Assert.False(SessionState.CanRestoreImpersonator(Make(RoleId.Admin, active: false)));
            Assert.False(SessionState.CanRestoreImpersonator(Make(RoleId.Admin, deleted: true)));
        }

        [Theory]
        [InlineData(RoleId.Yonetici)]
        [InlineData(RoleId.BM)]
        [InlineData(RoleId.ST)]
        public void Admin_Olmayan_Taklit_Eden_Geri_Yuklenmez(int rol)
        {
            // Rolü sonradan düşürülen eski admin, saklı impersonator_id ile ST olarak devam edemez.
            Assert.False(SessionState.CanRestoreImpersonator(Make(rol)));
        }
    }
}
