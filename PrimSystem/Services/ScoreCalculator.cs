namespace PrimSystem.Services
{
    /// <summary>
    /// Prim skoru iş kuralları — saf (yan etkisiz) fonksiyonlar.
    /// CalculationService bu sınıfı kullanır; böylece skor matematiği DB'den
    /// bağımsız olarak birim test edilebilir.
    /// </summary>
    public static class ScoreCalculator
    {
        /// <summary>Ürün başına kabul edilen azami oran: hedefin %105'i.</summary>
        public const decimal MaxProductRate = 1.05m;

        /// <summary>Prim hak ediş eşiği (final skor).</summary>
        public const decimal GateThreshold = 0.85m;

        public readonly record struct ProductScore(decimal Rate, decimal CappedRate, decimal Score, bool IsCapped);

        /// <summary>Tek ürün için oran, cap'li oran, ağırlıklı skor ve cap bayrağı.</summary>
        public static ProductScore ScoreProduct(decimal targetQty, decimal actualQty, int weight)
        {
            decimal rate = targetQty > 0 ? actualQty / targetQty : 0m;
            // İade (net-negatif satış) oranı 0'a tabanlanır: iade satışı sıfıra kadar düşürür
            // ama skoru eksiye geçirmez. Üst sınır %105 (cap). Ham Rate şeffaflık için korunur.
            decimal capped = Math.Clamp(rate, 0m, MaxProductRate);
            decimal score = capped * weight / 100m;
            return new ProductScore(rate, capped, score, rate > MaxProductRate);
        }

        /// <summary>
        /// Scorecard satır düzenlemesinde hiçbir alan değişmemiş mi? (ağırlık, min oran, ürün grubu,
        /// prim'e tabi, import alias). Alias'ta null/boş eşdeğer sayılır ve baştaki/sondaki boşluk yok sayılır.
        /// Amaç: bağlama (binding) bozulduğunda "kaydettim ama değişmedi" durumunun SESSİZ kalmaması —
        /// aynı değer yazılacaksa kullanıcı uyarılır.
        /// </summary>
        public static bool ScorecardEditIsNoOp(
            int oldWeight, int newWeight,
            decimal oldMinRate, decimal newMinRate,
            string? oldGroup, string? newGroup,
            bool oldPrim, bool newPrim,
            string? oldAlias, string? newAlias)
            => oldWeight == newWeight
               && oldMinRate == newMinRate
               && string.Equals((oldGroup ?? "").Trim(), (newGroup ?? "").Trim(), StringComparison.Ordinal)
               && oldPrim == newPrim
               && string.Equals((oldAlias ?? "").Trim(), (newAlias ?? "").Trim(), StringComparison.Ordinal);

        /// <summary>Dağıtım skoru: satış yapılan brick / atanan toplam brick (cap %100).</summary>
        public static decimal DistributionScore(int soldBricks, int totalBricks)
            => totalBricks > 0 ? Math.Min((decimal)soldBricks / totalBricks, 1.0m) : 0m;

        // ── Run-rate (dönem açıklığı) ────────────────────────────────────────────

        /// <summary>
        /// Sell-out verisi olan dönem sayısının çeyrekteki toplam döneme oranı (0-1).
        /// Hedef bu oranla küçültülerek "run-rate" karşılaştırması yapılır: 3 aylık hedef ile
        /// yalnız 2 ayın satışını kıyaslama adaletsizliğini giderir (örn. 2/3 ≈ 0,667).
        /// totalPeriods 0 ise 1 döner (etkisiz).
        /// </summary>
        public static decimal SoldPeriodRatio(int soldPeriods, int totalPeriods)
            => totalPeriods > 0 ? Math.Clamp((decimal)soldPeriods / totalPeriods, 0m, 1m) : 1m;

        /// <summary>Run-rate'e göre orantılanmış hedef (targetQty × ratio).</summary>
        public static decimal ProrateTarget(decimal targetQty, decimal ratio) => targetQty * ratio;

        /// <summary>
        /// Strike skoru: planlanan GLN'lerden kaçına sipariş yapıldı (cap %100).
        /// Plan yoksa (plannedCount &lt;= 0) null döner — hesaba dahil edilmez.
        /// </summary>
        public static decimal? StrikeScore(int plannedCount, int matchedCount)
        {
            if (plannedCount <= 0) return null;
            return Math.Min((decimal)matchedCount / plannedCount, 1.0m);
        }

        /// <summary>4 KPI'lı ağırlıklı final skor. orderScore null ise 0 olarak girer.</summary>
        public static decimal FinalScore(
            decimal salesScore, decimal rutaScore, decimal distScore, decimal? orderScore,
            decimal salesWeight, decimal rutaWeight, decimal distWeight, decimal orderWeight)
            => (salesScore * salesWeight)
             + (rutaScore * rutaWeight)
             + (distScore * distWeight)
             + ((orderScore ?? 0m) * orderWeight);

        public static bool PassedGate(decimal finalScore) => finalScore >= GateThreshold;

        /// <summary>
        /// Çeyrek başlangıcı ile işe giriş arasındaki tamamlanmış ay sayısı.
        /// İşe giriş ayın ortasındaysa (gün &gt; 1) o ay tamamlanmamış sayılır.
        /// </summary>
        public static int MonthsWorked(DateTime hireDate, DateTime quarterStart)
        {
            int months = ((quarterStart.Year - hireDate.Year) * 12) + quarterStart.Month - hireDate.Month;
            if (hireDate.Day > 1) months--;
            return months;
        }

        public static bool IsEligible(DateTime hireDate, DateTime quarterStart, int minMonths)
            => MonthsWorked(hireDate, quarterStart) >= minMonths;

        // ── Prim Tutarı ────────────────────────────────────────────────────────

        /// <summary>
        /// Final skorun düştüğü kademenin çarpanını döndürür (MinScore &lt;= skor &lt;= MaxScore).
        /// Hiçbir kademeye uymuyorsa null.
        /// </summary>
        public static decimal? TierMultiplier(decimal finalScore, IEnumerable<(decimal Min, decimal Max, decimal Mult)> tiers)
        {
            foreach (var t in tiers)
                if (finalScore >= t.Min && finalScore <= t.Max)
                    return t.Mult;
            return null;
        }

        /// <summary>
        /// Prim tutarı (TL). Hak ediş yoksa (eligible değil veya gate geçilmedi) ya da
        /// skor hiçbir kademeye uymuyorsa 0 döner. Aksi halde taban × çarpan (2 ondalığa yuvarlı).
        /// </summary>
        public static decimal PrimAmount(
            decimal finalScore, decimal baseAmount, bool isEligible, bool passedGate,
            IEnumerable<(decimal Min, decimal Max, decimal Mult)> tiers)
        {
            if (!isEligible || !passedGate) return 0m;
            var mult = TierMultiplier(finalScore, tiers);
            return mult.HasValue ? Math.Round(baseAmount * mult.Value, 2) : 0m;
        }
    }
}
