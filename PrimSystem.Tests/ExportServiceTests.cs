using OfficeOpenXml;
using PrimSystem.Data;
using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    // ExportReportAsync ve ExportPharmacies DB kullanmaz (yalnız verilen veriyi biçimler).
    // Bu testler biçimlendirme (başlık/dondurma/otomatik-filtre/SUBTOTAL toplam) çalışma
    // zamanında EPPlus'ı patlatmadan geçerli .xlsx üretiyor mu doğrular.
    public class ExportServiceTests
    {
        static ExportServiceTests() => ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        private static ExportService Svc() => new ExportService(factory: null!); // bu metotlar _factory'ye dokunmaz

        private static CalcSummary MakeSummary(string name, string region, bool eligible, bool passed,
            decimal sales, decimal final, decimal prim)
        {
            var calc = new Calculation
            {
                Year = 2026, Quarter = "Q2",
                TotalScore = sales, RutaScore = 0.9m, DistScore = 0.8m, OrderScore = 0.7m,
                FinalScore = final, PassedGate = passed, IsEligible = eligible, PrimAmount = prim,
                User = new User { FullName = name, Region = new Region { Name = region } }
            };
            var details = new List<CalculationDetail>
            {
                new() { ProductId = 1, TargetQty = 100, ActualQty = 95, Rate = 0.95m,
                        Product = new Product { ProductId = 1, Name = "CAREFREE", ProductGroup = "ITR", SortOrder = 1 } },
                new() { ProductId = 2, TargetQty = 50, ActualQty = 60, Rate = 1.2m, IsCapped = true,
                        Product = new Product { ProductId = 2, Name = "NEUTROGENA", ProductGroup = "OTC", SortOrder = 2 } },
            };
            return new CalcSummary { Calc = calc, Details = details };
        }

        [Fact]
        public void ExportReport_GecerliXlsx_4Sayfa_VeToplamFormulu()
        {
            var data = new[]
            {
                MakeSummary("Ali Veli", "EGE", eligible: true, passed: true, sales: 0.92m, final: 0.90m, prim: 12000m),
                MakeSummary("Ayşe Demir", "EGE", eligible: true, passed: false, sales: 0.70m, final: 0.72m, prim: 0m),
                MakeSummary("Yeni ST", "MARMARA", eligible: false, passed: false, sales: 0.40m, final: 0.45m, prim: 0m),
            };

            var bytes = Svc().ExportReportAsync(2026, "Q2", data);
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 0);

            using var pkg = new ExcelPackage(new MemoryStream(bytes));
            // Özet en başta
            Assert.Equal("Özet", pkg.Workbook.Worksheets[0].Name);
            var ws = pkg.Workbook.Worksheets["ST Listesi"];
            Assert.NotNull(ws);
            // Başlık 2. satırda
            Assert.Equal("İsim", ws.Cells[2, 2].Value);
            // 3 veri satırı (3,4,5) + toplam satırı (6) → SUBTOTAL formülü
            var totalFormula = ws.Cells[6, 9].Formula;
            Assert.Contains("SUBTOTAL", totalFormula);
            // Donmuş bölme uygulanmış olmalı (başlık altı)
            Assert.True(ws.View.PaneSettings != null);
        }

        [Fact]
        public void ExportPharmacies_GecerliXlsx_ToplamSatiri()
        {
            var rows = new[]
            {
                new PharmacyRow(1, 10, "BRICK_A", "1234567890123", "ECZANE BİR", "İZMİR", "KONAK", 120m, 5400m),
                new PharmacyRow(2, 10, "BRICK_A", "9876543210987", "ECZANE İKİ", "İZMİR", "BORNOVA", 80m, 3600m),
            };
            var bytes = Svc().ExportPharmacies(2026, "Q2", rows);
            Assert.True(bytes.Length > 0);

            using var pkg = new ExcelPackage(new MemoryStream(bytes));
            var ws = pkg.Workbook.Worksheets[0];
            // Kutu toplamı SUBTOTAL formülü (satır: başlık2 + 2 veri = 4 → toplam 5)
            Assert.Contains("SUBTOTAL", ws.Cells[5, 6].Formula);
        }

        [Fact]
        public void ExportReport_BosListe_PatlamadanXlsxUretir()
        {
            var bytes = Svc().ExportReportAsync(2026, "Q2", Array.Empty<CalcSummary>());
            Assert.True(bytes.Length > 0);
        }

        [Fact]
        public void ExportBricks_GecerliXlsx_Baslik_OrtakVeAtanmamis()
        {
            var rows = new[]
            {
                new BrickExportRow("ANK KIZILAY", "ANK", "İç Anadolu", "SEVGİ İNANÇ", 60m, true),
                new BrickExportRow("ANK KIZILAY", "ANK", "İç Anadolu", "TARIK KIZILKAYA", 40m, true),
                new BrickExportRow("ANK BALGAT", "ANK", "İç Anadolu", "TARIK KIZILKAYA", 100m, false),
                new BrickExportRow("ADANA CUKUROVA", "ADANA", "Akdeniz", "Atanmamış", null, false),
            };
            var bytes = Svc().ExportBricks(rows);
            Assert.True(bytes.Length > 0);

            using var pkg = new ExcelPackage(new MemoryStream(bytes));
            var ws = pkg.Workbook.Worksheets["Brick Yapısı"];
            Assert.NotNull(ws);
            // Başlık 2. satırda
            Assert.Equal("Brick Adı", ws.Cells[2, 1].Value);
            Assert.Equal("Atanmış ST", ws.Cells[2, 4].Value);
            // 4 veri satırı (3..6)
            Assert.Equal(6, ws.Dimension.End.Row);
        }

        [Fact]
        public void ExportBricks_BosListe_PatlamadanXlsxUretir()
        {
            var bytes = Svc().ExportBricks(Array.Empty<BrickExportRow>());
            Assert.True(bytes.Length > 0);
        }

        // ── Ham hesaplama dökümü: DİKEY düzen (her satır = ST × ürün) ───────────

        [Fact]
        public void BuildCalculationsVertical_Basliklar1SatirdaVe14Kolon()
        {
            var bytes = Svc().BuildCalculationsVertical(2026, "Q2",
                new[] { MakeSummary("Ali Veli", "EGE", eligible: true, passed: true, sales: 0.92m, final: 0.90m, prim: 12000m) });

            using var pkg = new ExcelPackage(new MemoryStream(bytes));
            var ws = pkg.Workbook.Worksheets["2026-Q2"];
            Assert.NotNull(ws);

            var beklenen = new[]
            {
                "ST Adı", "Ürün", "Hedef", "Gerçekleşen", "Gerç. %",
                "Bölge", "Durum", "Satış %", "Ruta %", "Dağıtım %", "Sipariş %", "Final %", "Geçit", "Prim (₺)"
            };
            for (int i = 0; i < beklenen.Length; i++)
                Assert.Equal(beklenen[i], ws.Cells[1, i + 1].Value);   // üst başlık YOK → 1. satır
            Assert.Equal(14, ws.Dimension.End.Column);
        }

        [Fact]
        public void BuildCalculationsVertical_UrunBasinaSatir_StKolonlariTekrarEder()
        {
            var data = new[]
            {
                MakeSummary("Ali Veli", "EGE", eligible: true, passed: true, sales: 0.92m, final: 0.90m, prim: 12000m),
                MakeSummary("Ayşe Demir", "MARMARA", eligible: true, passed: false, sales: 0.70m, final: 0.72m, prim: 0m),
            };
            var bytes = Svc().BuildCalculationsVertical(2026, "Q2", data);

            using var pkg = new ExcelPackage(new MemoryStream(bytes));
            var ws = pkg.Workbook.Worksheets["2026-Q2"];

            // 2 ST × 2 ürün = 4 veri satırı (2..5) + ÖZET (6)
            Assert.Equal(6, ws.Dimension.End.Row);

            // 1. ST'nin iki ürün satırı
            Assert.Equal("Ali Veli", ws.Cells[2, 1].Value);
            Assert.Equal("CAREFREE", ws.Cells[2, 2].Value);
            Assert.Equal("Ali Veli", ws.Cells[3, 1].Value);
            Assert.Equal("NEUTROGENA", ws.Cells[3, 2].Value);

            // Ürün-seviyesi değerler satırdan satıra DEĞİŞİR
            Assert.Equal(100d, ws.Cells[2, 3].Value);   // Hedef
            Assert.Equal(50d, ws.Cells[3, 3].Value);

            // ST-seviyesi değerler AYNI ST'nin her satırında TEKRAR EDER
            Assert.Equal(0.90d, ws.Cells[2, 12].Value); // Final %
            Assert.Equal(0.90d, ws.Cells[3, 12].Value);
            Assert.Equal(12000d, ws.Cells[2, 14].Value); // Prim
            Assert.Equal(12000d, ws.Cells[3, 14].Value);
            Assert.Equal("Geçti", ws.Cells[2, 7].Value);
            Assert.Equal("EVET", ws.Cells[2, 13].Value);

            // 2. ST kendi bloğunda
            Assert.Equal("Ayşe Demir", ws.Cells[4, 1].Value);
            Assert.Equal("Kaldı", ws.Cells[4, 7].Value);
            Assert.Equal("HAYIR", ws.Cells[4, 13].Value);
        }

        [Fact]
        public void BuildCalculationsVertical_OzetSatiri_YalnizUrunKolonlari()
        {
            var data = new[]
            {
                MakeSummary("Ali Veli", "EGE", eligible: true, passed: true, sales: 0.92m, final: 0.90m, prim: 12000m),
            };
            var bytes = Svc().BuildCalculationsVertical(2026, "Q2", data);

            using var pkg = new ExcelPackage(new MemoryStream(bytes));
            var ws = pkg.Workbook.Worksheets["2026-Q2"];
            int tr = ws.Dimension.End.Row; // ÖZET satırı

            Assert.Contains("SUBTOTAL(109", ws.Cells[tr, 3].Formula);  // Hedef → toplam
            Assert.Contains("SUBTOTAL(109", ws.Cells[tr, 4].Formula);  // Gerçekleşen → toplam
            Assert.Contains("SUBTOTAL(101", ws.Cells[tr, 5].Formula);  // Gerç. % → ortalama

            // ST-seviyesi kolonlar ÖZET'te BOŞ (tekrar ettikleri için toplanamaz)
            Assert.True(string.IsNullOrEmpty(ws.Cells[tr, 12].Formula)); // Final %
            Assert.True(string.IsNullOrEmpty(ws.Cells[tr, 14].Formula)); // Prim ₺
            Assert.Null(ws.Cells[tr, 14].Value);
        }

        [Fact]
        public void BuildCalculationsVertical_BosListe_PatlamadanXlsxUretir()
        {
            var bytes = Svc().BuildCalculationsVertical(2026, "Q2", Array.Empty<CalcSummary>());
            Assert.True(bytes.Length > 0);
        }
    }
}
