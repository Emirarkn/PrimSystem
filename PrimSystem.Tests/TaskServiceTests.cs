using PrimSystem.Data;
using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class TaskServiceTests
    {
        private static User Make(int role, int? region, bool active = true, bool deleted = false) =>
            new User { RoleId = role, RegionId = region, IsActive = active, IsDeleted = deleted };

        [Fact]
        public void BM_Kendi_Bolge_ST_sine_Atayabilir()
        {
            var bm = Make(RoleId.BM, region: 5);
            var st = Make(RoleId.ST, region: 5);
            Assert.True(TaskService.CanAssignTo(bm, st));
        }

        [Fact]
        public void BM_Baska_Bolge_ST_sine_Atayamaz()
        {
            var bm = Make(RoleId.BM, region: 5);
            var st = Make(RoleId.ST, region: 9);
            Assert.False(TaskService.CanAssignTo(bm, st));
        }

        [Fact]
        public void BM_ST_Olmayan_Kullaniciya_Atayamaz()
        {
            var bm = Make(RoleId.BM, region: 5);
            var digerBm = Make(RoleId.BM, region: 5);
            Assert.False(TaskService.CanAssignTo(bm, digerBm));
        }

        [Fact]
        public void BM_Pasif_Veya_Silinmis_ST_ye_Atayamaz()
        {
            var bm = Make(RoleId.BM, region: 5);
            Assert.False(TaskService.CanAssignTo(bm, Make(RoleId.ST, region: 5, active: false)));
            Assert.False(TaskService.CanAssignTo(bm, Make(RoleId.ST, region: 5, deleted: true)));
        }

        [Fact]
        public void Admin_Herhangi_Bir_ST_ye_Atayabilir()
        {
            var admin = Make(RoleId.Admin, region: null);
            Assert.True(TaskService.CanAssignTo(admin, Make(RoleId.ST, region: 1)));
            Assert.True(TaskService.CanAssignTo(admin, Make(RoleId.ST, region: 42)));
        }

        [Fact]
        public void Yonetici_Herhangi_Bir_ST_ye_Atayabilir()
        {
            var yon = Make(RoleId.Yonetici, region: null);
            Assert.True(TaskService.CanAssignTo(yon, Make(RoleId.ST, region: 7)));
        }

        [Fact]
        public void ST_Gorev_Atayamaz()
        {
            var st = Make(RoleId.ST, region: 5);
            Assert.False(TaskService.CanAssignTo(st, Make(RoleId.ST, region: 5)));
        }
    }
}
