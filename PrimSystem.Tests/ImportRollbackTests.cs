using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class ImportRollbackTests
    {
        [Theory]
        [InlineData("SellOut")]
        [InlineData("Sipariş")]
        [InlineData("Hedef")]
        [InlineData("Ruta")]
        [InlineData("ZiyaretPlan")]
        [InlineData("EczanePlani")]
        public void Satir_Silinebilen_Turlerde_Geri_Alma_Desteklenir(string tur)
        {
            Assert.True(ImportService.SupportsRollback(tur));
        }

        [Theory]
        [InlineData("BrickAtama")]
        [InlineData("UrunMarka")]
        [InlineData("")]
        [InlineData(null)]
        public void Eslestirme_Ve_Bilinmeyen_Turlerde_Geri_Alma_Desteklenmez(string? tur)
        {
            // Bu türlerde satır silmek önceki eşleştirmeyi geri getirmez; log da silinmemeli (F11).
            Assert.False(ImportService.SupportsRollback(tur));
        }
    }
}
