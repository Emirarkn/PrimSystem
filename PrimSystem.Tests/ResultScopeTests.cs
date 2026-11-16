using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class ResultScopeTests
    {
        [Fact]
        public void BM_Bolgesi_Varsa_Kendi_Bolgesine_Filtrelenir()
        {
            Assert.Equal(2, ResultScope.RegionFilter("BM", 2));
        }

        [Fact]
        public void BM_Bolgesi_Yoksa_Hicbir_Bolgeyle_Eslesmez_Null_Donmez()
        {
            // null "tüm bölgeler" demek: bölgesiz BM herkesin primini görürdü (SEC-001).
            var filtre = ResultScope.RegionFilter("BM", null);
            Assert.NotNull(filtre);
            Assert.Equal(ResultScope.NoRegion, filtre);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void BM_Gecersiz_Bolge_Id_si_Eslesmez_Degere_Cevrilir(int regionId)
        {
            Assert.Equal(ResultScope.NoRegion, ResultScope.RegionFilter("BM", regionId));
        }

        [Theory]
        [InlineData("Admin")]
        [InlineData("Yonetici")]
        [InlineData("ST")]
        [InlineData("")]
        public void BM_Disindaki_Roller_Bolge_Filtresi_Almaz(string rol)
        {
            Assert.Null(ResultScope.RegionFilter(rol, 2));
            Assert.Null(ResultScope.RegionFilter(rol, null));
        }
    }
}
