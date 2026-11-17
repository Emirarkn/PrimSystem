using PrimSystem.Data;
using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class ResultAccessTests
    {
        [Fact]
        public void ST_Yalniz_Kendi_Yayinlanmis_Satirini_Gorur_Istek_Ne_Olursa_Olsun()
        {
            // Sayfa yanlışlıkla filtre yollamasa / başka kullanıcı ve taslak istese bile.
            var q = ResultAccess.Resolve(RoleId.ST, actorUserId: 7, actorRegionId: 2,
                requestedPublishedOnly: false, requestedRegionId: 3, requestedUserId: 99);
            Assert.Equal(7, q.UserId);
            Assert.Null(q.RegionId);
            Assert.True(q.PublishedOnly);
        }

        [Fact]
        public void BM_Kendi_Bolgesine_Ve_Yalniz_Yayinlanmisa_Kilitlenir()
        {
            var q = ResultAccess.Resolve(RoleId.BM, 5, actorRegionId: 2,
                requestedPublishedOnly: false, requestedRegionId: 3, requestedUserId: null);
            Assert.Equal(2, q.RegionId);   // istenen bölge (3) yok sayılır
            Assert.True(q.PublishedOnly);  // taslak istenemez
        }

        [Fact]
        public void BM_Bolgesi_Yoksa_Hicbir_Bolgeyle_Eslesmez()
        {
            var q = ResultAccess.Resolve(RoleId.BM, 5, actorRegionId: null,
                requestedPublishedOnly: true, requestedRegionId: null, requestedUserId: null);
            Assert.Equal(ResultScope.NoRegion, q.RegionId);
        }

        [Theory]
        [InlineData(RoleId.Admin)]
        [InlineData(RoleId.Yonetici)]
        public void Admin_Ve_Yonetici_Istedigi_Kadar_Daraltabilir_Ve_Taslagi_Gorebilir(int rol)
        {
            var genis = ResultAccess.Resolve(rol, 1, null, false, null, null);
            Assert.Null(genis.RegionId);
            Assert.Null(genis.UserId);
            Assert.False(genis.PublishedOnly);

            var dar = ResultAccess.Resolve(rol, 1, null, true, 4, 12);
            Assert.Equal(4, dar.RegionId);
            Assert.Equal(12, dar.UserId);
            Assert.True(dar.PublishedOnly);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(5)]
        [InlineData(-1)]
        public void Bilinmeyen_Rol_Reddedilir(int rol)
        {
            var q = ResultAccess.Resolve(rol, 9, 2, false, null, null);
            Assert.Equal(ResultAccess.Denied, q);
            Assert.Equal(-1, q.UserId);
            Assert.Equal(ResultScope.NoRegion, q.RegionId);
        }
    }
}
