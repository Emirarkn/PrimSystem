using PrimSystem.Data;
using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests;

public class ScoreCalculatorTests
{
    // ── ScoreProduct ─────────────────────────────────────────────────────────

    [Fact]
    public void ScoreProduct_HedefSifirsa_OranVeSkorSifir()
    {
        var r = ScoreCalculator.ScoreProduct(targetQty: 0, actualQty: 100, weight: 60);
        Assert.Equal(0m, r.Rate);
        Assert.Equal(0m, r.CappedRate);
        Assert.Equal(0m, r.Score);
        Assert.False(r.IsCapped);
    }

    [Fact]
    public void ScoreProduct_TamUlasim_AgirliktanGelenSkor()
    {
        // 100/100 = 1.0 oran, ağırlık 60 → skor 0.60
        var r = ScoreCalculator.ScoreProduct(100, 100, 60);
        Assert.Equal(1.0m, r.Rate);
        Assert.Equal(1.0m, r.CappedRate);
        Assert.Equal(0.60m, r.Score);
        Assert.False(r.IsCapped);
    }

    [Fact]
    public void ScoreProduct_AsiriUlasim_Yuzde105teCaplenir()
    {
        // 200/100 = 2.0 oran → cap 1.05; ağırlık 60 → skor 1.05*0.60 = 0.63
        var r = ScoreCalculator.ScoreProduct(100, 200, 60);
        Assert.Equal(2.0m, r.Rate);
        Assert.Equal(1.05m, r.CappedRate);
        Assert.Equal(0.63m, r.Score);
        Assert.True(r.IsCapped);
    }

    [Fact]
    public void ScoreProduct_TamYuzde105_CaplenmisSayilmaz()
    {
        // Sınır: oran tam 1.05 → cap değil (rate > maxRate false)
        var r = ScoreCalculator.ScoreProduct(100, 105, 50);
        Assert.Equal(1.05m, r.Rate);
        Assert.Equal(1.05m, r.CappedRate);
        Assert.False(r.IsCapped);
    }

    [Fact]
    public void ScoreProduct_NetNegatifIade_CappedVeSkorSifir_HamRateNegatifKalir()
    {
        // İade satışı aşarsa: ham Rate negatif (şeffaflık), ama CappedRate ve Skor 0'a tabanlanır
        var r = ScoreCalculator.ScoreProduct(targetQty: 100, actualQty: -20, weight: 60);
        Assert.Equal(-0.2m, r.Rate);
        Assert.Equal(0m, r.CappedRate);
        Assert.Equal(0m, r.Score);
        Assert.False(r.IsCapped);
    }

    // ── Run-rate (SoldPeriodRatio / ProrateTarget) ────────────────────────────

    [Fact]
    public void SoldPeriodRatio_IkiAyUcAydan_UcteIki()
        => Assert.Equal(2m / 3m, ScoreCalculator.SoldPeriodRatio(soldPeriods: 2, totalPeriods: 3));

    [Fact]
    public void SoldPeriodRatio_TumDonemlerDolu_Bir()
        => Assert.Equal(1m, ScoreCalculator.SoldPeriodRatio(3, 3));

    [Fact]
    public void SoldPeriodRatio_ToplamSifir_BirDoner_Etkisiz()
        => Assert.Equal(1m, ScoreCalculator.SoldPeriodRatio(0, 0));

    [Fact]
    public void SoldPeriodRatio_FazlaDonem_BirdeCaplenir()
        => Assert.Equal(1m, ScoreCalculator.SoldPeriodRatio(4, 3));

    [Fact]
    public void ProrateTarget_OranlaCarpar()
        => Assert.Equal(46206.0m, ScoreCalculator.ProrateTarget(69309m, 2m / 3m), precision: 0);

    [Fact]
    public void ProrateTarget_RunRateIleRealizasyonYukselir()
    {
        // 3 aylık hedef 300, 2 ayda 200 satış. Ham oran 200/300=0.667; run-rate (×2/3)
        // ile hedef 200'e iner → oran 1.0 (adil). Skor ağırlık 60 → 0.60.
        decimal ratio = ScoreCalculator.SoldPeriodRatio(2, 3);
        decimal prorated = ScoreCalculator.ProrateTarget(300m, ratio);
        var ham = ScoreCalculator.ScoreProduct(300m, 200m, 60);
        var runrate = ScoreCalculator.ScoreProduct(prorated, 200m, 60);
        Assert.True(runrate.Score > ham.Score);
        Assert.Equal(1.0m, runrate.CappedRate);
    }

    // ── DistributionScore ─────────────────────────────────────────────────────

    [Fact]
    public void DistributionScore_AtanmisBrickYoksa_Sifir()
        => Assert.Equal(0m, ScoreCalculator.DistributionScore(soldBricks: 5, totalBricks: 0));

    [Fact]
    public void DistributionScore_Orantili()
        => Assert.Equal(0.5m, ScoreCalculator.DistributionScore(5, 10));

    [Fact]
    public void DistributionScore_YuzdeYuzdeCaplenir()
        => Assert.Equal(1.0m, ScoreCalculator.DistributionScore(12, 10));

    // ── StrikeScore ───────────────────────────────────────────────────────────

    [Fact]
    public void StrikeScore_PlanYoksa_Null()
        => Assert.Null(ScoreCalculator.StrikeScore(plannedCount: 0, matchedCount: 0));

    [Fact]
    public void StrikeScore_PlanVarSiparisYok_Sifir()
        => Assert.Equal(0m, ScoreCalculator.StrikeScore(10, 0));

    [Fact]
    public void StrikeScore_Orantili()
        => Assert.Equal(0.4m, ScoreCalculator.StrikeScore(10, 4));

    [Fact]
    public void StrikeScore_YuzdeYuzdeCaplenir()
        => Assert.Equal(1.0m, ScoreCalculator.StrikeScore(10, 15));

    // ── FinalScore + Gate ─────────────────────────────────────────────────────

    [Fact]
    public void FinalScore_VarsayilanAgirliklarla()
    {
        // Satış 0.90*0.60 + Ruta 0.80*0.15 + Dağıtım 1.0*0.15 + Strike 0.50*0.10
        // = 0.54 + 0.12 + 0.15 + 0.05 = 0.86
        var f = ScoreCalculator.FinalScore(0.90m, 0.80m, 1.0m, 0.50m, 0.60m, 0.15m, 0.15m, 0.10m);
        Assert.Equal(0.86m, f);
        Assert.True(ScoreCalculator.PassedGate(f));
    }

    [Fact]
    public void FinalScore_StrikeNull_SifirGirer()
    {
        var withNull = ScoreCalculator.FinalScore(0.90m, 0.80m, 1.0m, null, 0.60m, 0.15m, 0.15m, 0.10m);
        var withZero = ScoreCalculator.FinalScore(0.90m, 0.80m, 1.0m, 0m, 0.60m, 0.15m, 0.15m, 0.10m);
        Assert.Equal(withZero, withNull);
    }

    [Theory]
    [InlineData(0.84, false)]
    [InlineData(0.85, true)]   // eşik dahil
    [InlineData(0.86, true)]
    public void PassedGate_Esik085Dahil(double score, bool expected)
        => Assert.Equal(expected, ScoreCalculator.PassedGate((decimal)score));

    // ── Eligibility / MonthsWorked ────────────────────────────────────────────

    [Fact]
    public void MonthsWorked_AyinBasindaIseGiren_TamAySayilir()
    {
        // 1 Oca işe giriş, çeyrek başı 1 Nis → 3 ay
        var months = ScoreCalculator.MonthsWorked(new DateTime(2026, 1, 1), new DateTime(2026, 4, 1));
        Assert.Equal(3, months);
    }

    [Fact]
    public void MonthsWorked_AyOrtasindaIseGiren_BirAzaltilir()
    {
        // 15 Oca işe giriş → o ay tamamlanmamış → 2 ay
        var months = ScoreCalculator.MonthsWorked(new DateTime(2026, 1, 15), new DateTime(2026, 4, 1));
        Assert.Equal(2, months);
    }

    [Theory]
    [InlineData("2026-01-01", 3, true)]   // 3 ay >= 3
    [InlineData("2026-02-01", 3, false)]  // 2 ay < 3
    [InlineData("2026-01-15", 3, false)]  // 2 ay (yarım ay düşülür) < 3
    public void IsEligible_MinAyKontrolu(string hire, int minMonths, bool expected)
    {
        var eligible = ScoreCalculator.IsEligible(DateTime.Parse(hire), new DateTime(2026, 4, 1), minMonths);
        Assert.Equal(expected, eligible);
    }

    // ── Prim Tutarı ───────────────────────────────────────────────────────────

    private static readonly (decimal Min, decimal Max, decimal Mult)[] Tiers =
    {
        (0.85m, 0.9499m, 0.50m),
        (0.95m, 0.9999m, 0.75m),
        (1.00m, 9.9999m, 1.00m),
    };

    [Theory]
    [InlineData(0.90, 0.50)]
    [InlineData(0.97, 0.75)]
    [InlineData(1.02, 1.00)]
    public void TierMultiplier_DogruKademe(double score, double expectedMult)
        => Assert.Equal((decimal)expectedMult, ScoreCalculator.TierMultiplier((decimal)score, Tiers));

    [Fact]
    public void TierMultiplier_HicbirKademeyeUymazsa_Null()
        => Assert.Null(ScoreCalculator.TierMultiplier(0.80m, Tiers));

    [Fact]
    public void PrimAmount_HakEdis_TabanCarpanCarpimi()
    {
        // skor 0.90 → çarpan 0.50; taban 10000 → 5000
        var prim = ScoreCalculator.PrimAmount(0.90m, 10000m, isEligible: true, passedGate: true, Tiers);
        Assert.Equal(5000m, prim);
    }

    [Fact]
    public void PrimAmount_GateGecilmediyse_Sifir()
        => Assert.Equal(0m, ScoreCalculator.PrimAmount(0.90m, 10000m, isEligible: true, passedGate: false, Tiers));

    [Fact]
    public void PrimAmount_EligibleDegilse_Sifir()
        => Assert.Equal(0m, ScoreCalculator.PrimAmount(0.97m, 10000m, isEligible: false, passedGate: true, Tiers));

    [Fact]
    public void PrimAmount_KademeYoksa_Sifir()
        => Assert.Equal(0m, ScoreCalculator.PrimAmount(0.80m, 10000m, isEligible: true, passedGate: true, Tiers));

    [Fact]
    public void PrimAmount_IkiOndalikaYuvarlar()
    {
        // skor 1.0 → çarpan 1.0; taban 1234.567 → 1234.57
        var prim = ScoreCalculator.PrimAmount(1.0m, 1234.567m, true, true, Tiers);
        Assert.Equal(1234.57m, prim);
    }

    // ── Ağırlık geçerliliği: hangi çeyreği etkiler? ───────────────────────────
    // CalculateQuarterAsync ağırlıkları `ValidFrom <= quarterStart` ile süzer; çeyrek
    // başladıktan SONRA geçerli olan değişiklik o çeyreği ETKİLEMEZ. UI bunu önceden uyarır.

    [Fact]
    public void AffectsQuarter_CeyrekBasindanSonrakiDegisiklik_OCeyregiEtkilemez()
    {
        var q3Basi = new DateTime(2026, 7, 1);
        Assert.False(CalculationService.AffectsQuarter(q3Basi, 2026, "Q2")); // Q2 başı 01.04 → etkilenmez
        Assert.True(CalculationService.AffectsQuarter(q3Basi, 2026, "Q3"));  // tam çeyrek başı → dahil
        Assert.True(CalculationService.AffectsQuarter(q3Basi, 2026, "Q4"));
        Assert.False(CalculationService.AffectsQuarter(q3Basi, 2025, "Q4")); // geçmiş yıl → etkilenmez
    }

    [Fact]
    public void AffectsQuarter_GeriyeDonukTarih_TumCeyrekleriEtkiler()
    {
        var yilBasi = new DateTime(2026, 1, 1);
        Assert.True(CalculationService.AffectsQuarter(yilBasi, 2026, "Q1"));
        Assert.True(CalculationService.AffectsQuarter(yilBasi, 2026, "Q2"));
        Assert.True(CalculationService.AffectsQuarter(yilBasi, 2026, "Q4"));
    }

    // ── KPI seçimi: çift aktif kayıtta deterministik olmalı ───────────────────

    private static KpiCriteria Kpi(int id, string name, int weight) =>
        new() { CriteriaId = id, Name = name, Weight = weight, Year = 2026 };

    [Fact]
    public void PickKpi_CiftKayitta_EnSonOlusturulanKazanir()
    {
        var list = new[]
        {
            Kpi(9,  "Satış Adet (IMS)", 60),
            Kpi(13, "Satış Adet (IMS)", 45),   // daha yeni → bu kazanmalı
            Kpi(10, "Ruta Uyum (Focus)", 15),
        };

        Assert.Equal(45, CalculationService.PickKpi(list, "Satış", "Sales")!.Weight);
        Assert.Equal(15, CalculationService.PickKpi(list, "Ruta", "Route")!.Weight);
    }

    [Fact]
    public void PickKpi_EslesmeYoksa_NullDoner()
    {
        var list = new[] { Kpi(1, "Satış Adet (IMS)", 60) };
        Assert.Null(CalculationService.PickKpi(list, "Dağıtım", "Dist"));
    }

    // ── ConfigSnapshot: hesaplamanın dondurduğu ağırlıklar ───────────────────
    // Hesaplama detay ekranı bu değerleri gösterir; canlı ağırlıktan farklı olabilir
    // (ağırlık hesaplamadan sonra değiştirilmişse) ve bu DOĞRU davranıştır.

    [Fact]
    public void ParseSnapshotWeights_GecerliSnapshot_AdAgirlikEslesmesi()
    {
        const string json = """
        {"Weights":[{"Name":"BEN GAY OINT 50 G 1","Weight":10},{"Name":"VISINE","Weight":10}],
         "KPI":[{"Name":"Satış","Weight":60,"MinRate":85}]}
        """;

        var map = CalculationService.ParseSnapshotWeights(json);

        Assert.Equal(2, map.Count);
        Assert.Equal(10, map["BEN GAY OINT 50 G 1"]);
        Assert.Equal(10, map["VISINE"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{bozuk json")]
    [InlineData("{\"KPI\":[]}")]                 // Weights anahtarı yok
    [InlineData("{\"Weights\":\"dizi degil\"}")] // Weights dizi değil
    public void ParseSnapshotWeights_BozukVeyaEksik_BosSozlukDoner(string? json)
    {
        var map = CalculationService.ParseSnapshotWeights(json);
        Assert.Empty(map);
    }

    [Fact]
    public void ParseSnapshotWeights_EksikAlanliGirdiyiAtlar()
    {
        // Name'i olmayan / Weight'i sayı olmayan girdiler sessizce atlanır, diğerleri okunur.
        const string json = """
        {"Weights":[{"Weight":9},{"Name":"KM60"},{"Name":"LISTERINE","Weight":"abc"},{"Name":"UNISOM","Weight":7}]}
        """;

        var map = CalculationService.ParseSnapshotWeights(json);

        Assert.Single(map);
        Assert.Equal(7, map["UNISOM"]);
    }

    // ── Scorecard düzenlemesi: hiçbir şey değişmediyse yakala ────────────────
    // Bağlama (binding) bozulduğunda eski değer tekrar kaydediliyor ve kullanıcı
    // "kaydettim ama değişmedi" sanıyordu; bu guard durumu görünür kılar.

    [Fact]
    public void ScorecardEditIsNoOp_TumAlanlarAyni_True()
    {
        Assert.True(ScoreCalculator.ScorecardEditIsNoOp(
            5, 5, 0.85m, 0.85m, "ITR", "ITR", true, true, "CAREFREE", "CAREFREE"));
    }

    [Fact]
    public void ScorecardEditIsNoOp_AliasBosVeNull_EsdegerSayilir()
    {
        Assert.True(ScoreCalculator.ScorecardEditIsNoOp(
            5, 5, 0.85m, 0.85m, "OTC", "OTC", true, true, null, "  "));
    }

    [Theory]
    [InlineData(5, 8, 0.85, 0.85, "ITR", "ITR", true, true, "A", "A")]   // ağırlık değişti
    [InlineData(5, 5, 0.85, 0.90, "ITR", "ITR", true, true, "A", "A")]   // min oran değişti
    [InlineData(5, 5, 0.85, 0.85, "ITR", "OTC", true, true, "A", "A")]   // grup değişti
    [InlineData(5, 5, 0.85, 0.85, "ITR", "ITR", true, false, "A", "A")]  // prim değişti
    [InlineData(5, 5, 0.85, 0.85, "ITR", "ITR", true, true, "A", "B")]   // alias değişti
    public void ScorecardEditIsNoOp_TekAlanFarkliysa_False(
        int oldW, int newW, double oldMin, double newMin,
        string oldGroup, string newGroup, bool oldPrim, bool newPrim, string oldAlias, string newAlias)
    {
        Assert.False(ScoreCalculator.ScorecardEditIsNoOp(
            oldW, newW, (decimal)oldMin, (decimal)newMin, oldGroup, newGroup, oldPrim, newPrim, oldAlias, newAlias));
    }
}
