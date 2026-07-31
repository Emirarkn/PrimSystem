using Microsoft.EntityFrameworkCore;
using PrimSystem.Data;
using System.Data.Common;
using System.Text.Json;

namespace PrimSystem.Services
{
    public class CalculationService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly AuditService _audit;
        private readonly NotificationService _notifications;
        private readonly ILogger<CalculationService> _logger;

        public CalculationService(IDbContextFactory<AppDbContext> factory, AuditService audit, NotificationService notifications, ILogger<CalculationService> logger)
        {
            _factory = factory;
            _audit = audit;
            _notifications = notifications;
            _logger = logger;
        }

        // ── Yetki & input validasyonu yardımcıları ─────────────────────
        private static readonly HashSet<string> _validQuarters = new() { "Q1", "Q2", "Q3", "Q4" };

        private static void ValidateQuarter(string quarter)
        {
            if (!_validQuarters.Contains(quarter))
                throw new ArgumentException("Geçersiz quarter (Q1-Q4 olmalı).");
        }

        /// <summary>Çeyreğin başlangıç tarihi — ağırlık geçerlilik (ValidFrom) karşılaştırmaları için.</summary>
        internal static DateTime QuarterStart(int year, string quarter) => quarter switch
        {
            "Q1" => new DateTime(year, 1, 1),
            "Q2" => new DateTime(year, 4, 1),
            "Q3" => new DateTime(year, 7, 1),
            "Q4" => new DateTime(year, 10, 1),
            _ => DateTime.MaxValue
        };

        /// <summary>
        /// Belirtilen tarihten geçerli olacak bir ağırlık değişikliği, o çeyreğin hesaplamasına girer mi?
        /// CalculateQuarterAsync ağırlıkları <c>ValidFrom &lt;= quarterStart</c> ile süzdüğü için, çeyrek
        /// başladıktan SONRA geçerli olan bir değişiklik o çeyreği ETKİLEMEZ (sessiz dışlama).
        /// UI bu fonksiyonla kullanıcıyı önceden uyarır.
        /// </summary>
        internal static bool AffectsQuarter(DateTime effectiveFrom, int year, string quarter)
            => effectiveFrom <= QuarterStart(year, quarter);

        /// <summary>
        /// Hesaplamanın DONDURDUĞU ürün ağırlıklarını `Calculation.ConfigSnapshot` JSON'undan çıkarır
        /// (ürün adı → ağırlık). Hesaplama detay ekranı bu değerleri gösterir; böylece "ekranda 8 yazıyor
        /// ama hesap 10 kullanmış" tutarsızlığı imkânsız olur.
        /// Null/bozuk/beklenmeyen JSON'da BOŞ sözlük döner — asla istisna fırlatmaz.
        /// </summary>
        internal static Dictionary<string, int> ParseSnapshotWeights(string? configSnapshot)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(configSnapshot)) return result;
            try
            {
                using var doc = JsonDocument.Parse(configSnapshot);
                if (!doc.RootElement.TryGetProperty("Weights", out var weights)
                    || weights.ValueKind != JsonValueKind.Array) return result;

                foreach (var w in weights.EnumerateArray())
                {
                    if (!w.TryGetProperty("Name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                    // ValueKind kontrolü ŞART: TryGetInt32 sayı olmayan değerde false DÖNMEZ,
                    // InvalidOperationException fırlatır (JsonException değil → catch'e takılmaz).
                    if (!w.TryGetProperty("Weight", out var wEl)
                        || wEl.ValueKind != JsonValueKind.Number
                        || !wEl.TryGetInt32(out var weight)) continue;
                    var name = nameEl.GetString();
                    if (!string.IsNullOrEmpty(name)) result[name] = weight;
                }
            }
            catch (JsonException) { /* bozuk snapshot — boş sözlük döner, ekran canlı ağırlığa düşer */ }
            return result;
        }

        /// <summary>
        /// Ad parçalarından birine uyan KPI'yı DETERMİNİSTİK seçer: aynı ad için birden çok aktif
        /// kayıt varsa en son oluşturulan (max CriteriaId) kazanır — P39'daki ağırlık seçimiyle aynı yön.
        /// </summary>
        internal static KpiCriteria? PickKpi(IEnumerable<KpiCriteria> list, params string[] nameFragments)
            => list.Where(k => nameFragments.Any(f => k.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                   .OrderByDescending(k => k.CriteriaId)
                   .FirstOrDefault();

        private async Task EnsureAdminAsync(int userId)
        {
            using var db = _factory.CreateDbContext();
            var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId);
            if (actor == null || actor.RoleId != RoleId.Admin)
                throw new UnauthorizedAccessException("Bu işlem için yetkiniz yok.");
        }

        // ── Prim Yapılandırması (admin) ────────────────────────────────────────

        public async Task<List<PrimTier>> GetPrimTiersAsync()
        {
            using var db = _factory.CreateDbContext();
            return await db.PrimTiers.OrderBy(t => t.MinScore).ToListAsync();
        }

        public async Task SavePrimTiersAsync(List<PrimTier> tiers, int userId)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var existing = await db.PrimTiers.ToListAsync();
            db.PrimTiers.RemoveRange(existing);
            foreach (var t in tiers)
                db.PrimTiers.Add(new PrimTier
                {
                    MinScore = t.MinScore, MaxScore = t.MaxScore, Multiplier = t.Multiplier,
                    IsActive = t.IsActive, CreatedAt = DateTime.UtcNow, UpdatedBy = userId
                });
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "SavePrimTiers", "PrimTier", null, newValue: $"{tiers.Count} kademe güncellendi");
        }

        public async Task<Dictionary<int, decimal>> GetRolePrimBasesAsync()
        {
            using var db = _factory.CreateDbContext();
            return await db.RolePrimBases.ToDictionaryAsync(r => r.RoleId, r => r.BaseAmount);
        }

        public async Task SaveRolePrimBaseAsync(int roleId, decimal amount, int userId)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var row = await db.RolePrimBases.FirstOrDefaultAsync(r => r.RoleId == roleId);
            if (row == null)
                db.RolePrimBases.Add(new RolePrimBase { RoleId = roleId, BaseAmount = amount, UpdatedAt = DateTime.UtcNow, UpdatedBy = userId });
            else { row.BaseAmount = amount; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = userId; }
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "SaveRolePrimBase", "RolePrimBase", roleId, newValue: $"Rol {roleId} taban: {amount}");
        }

        // ── Genel Görüntüleme Ayarı: "Yalnız satış skoru" modu (admin) ──────────
        private const string SalesOnlyModeKey = "SalesOnlyMode";

        public async Task<bool> GetSalesOnlyModeAsync()
        {
            try
            {
                using var db = _factory.CreateDbContext();
                var setting = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SalesOnlyModeKey);
                return setting?.Value == "true";
            }
            catch (Exception ex)
            {
                // SystemSettings tablosu yoksa (db/system-settings.sql çalıştırılmadıysa) veya
                // geçici DB hatasında panoyu çökertme; mod kapalı varsay.
                _logger.LogWarning(ex, "SalesOnlyMode okunamadı (SystemSettings yok/erişilemez); mod kapalı varsayıldı.");
                return false;
            }
        }

        public async Task SetSalesOnlyModeAsync(bool enabled, int userId)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SalesOnlyModeKey);
            var val = enabled ? "true" : "false";
            if (setting == null)
                db.SystemSettings.Add(new SystemSetting { Key = SalesOnlyModeKey, Value = val, UpdatedAt = DateTime.UtcNow, UpdatedBy = userId });
            else { setting.Value = val; setting.UpdatedAt = DateTime.UtcNow; setting.UpdatedBy = userId; }
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "SetSalesOnlyMode", "SystemSetting", null, newValue: $"SalesOnlyMode = {val}");
        }

        // ── Hesaplama tazeliği: bir çeyreğin son hesaplaması brick atamalarına göre bayat mı,
        //    ve yayınlanmış mı? Rapor/ana ekran/Hesapla bandı bunu gösterir ("Yeniden Hesapla +
        //    Yayınla gerekli"). Brick ataması (UserBricks) yalnız yeniden hesaplamayla Calculation'a
        //    yansır; recalc de yayınlanmadıkça ST/prim ekranlarına düşmez. Salt-okunur, hataya dayanıklı.
        public async Task<CalcFreshness> GetCalcFreshnessAsync(int year, string quarter)
        {
            quarter = (quarter ?? "").Trim().ToUpperInvariant();
            if (!_validQuarters.Contains(quarter)) return new CalcFreshness(false, null, false, false);
            try
            {
                using var db = _factory.CreateDbContext();

                var lastCalc = await db.Calculations
                    .Where(c => c.Year == year && c.Quarter == quarter)
                    .MaxAsync(c => (DateTime?)c.CalculatedAt);

                bool published = lastCalc != null && await db.Calculations
                    .AnyAsync(c => c.Year == year && c.Quarter == quarter && c.IsPublished);

                // Herhangi bir brick atama değişikliği (atama VEYA kaldırma) — en yeni damga.
                // Nullable Max: satır yoksa null döner (boş dizide istisna atmaz).
                DateTime? lastAssignmentChange = await db.UserBricks
                    .Select(ub => (DateTime?)(ub.UnassignedAt != null && ub.UnassignedAt > ub.AssignedAt
                        ? ub.UnassignedAt.Value
                        : ub.AssignedAt))
                    .MaxAsync();

                // Ağırlık (scorecard + KPI) değişikliği — en son ekleme VEYA yerinde güncelleme zamanı.
                // "Geriye dönük" düzenleme satırı yerinde günceller (yeni satır yok) → yalnız CreatedAt'e
                // bakmak yetmez, UpdatedAt de hesaba katılır. Kolonlar yoksa (script/self-heal çalışmadıysa)
                // bu blok catch'e düşmez: ayrı try ile korunur.
                DateTime? lastWeightChange = null;
                try
                {
                    var wMax = await db.ScorecardWeights
                        .Select(w => w.UpdatedAt > w.CreatedAt ? w.UpdatedAt : (DateTime?)w.CreatedAt)
                        .MaxAsync();
                    var kMax = await db.KPICriteria
                        .Select(k => k.UpdatedAt)
                        .MaxAsync();
                    lastWeightChange = wMax > kMax ? wMax : (kMax ?? wMax);
                }
                catch { /* CreatedAt/UpdatedAt kolonu yok — ağırlık tazeliği atlanır, atama/yayın yine çalışır */ }

                // Bayat: hiç hesaplama yok VEYA atama/ağırlık son hesaplamadan sonra değişmiş.
                bool stale = lastCalc == null
                    || (lastAssignmentChange.HasValue && lastAssignmentChange.Value > lastCalc.Value)
                    || (lastWeightChange.HasValue && lastWeightChange.Value > lastCalc.Value);

                return new CalcFreshness(lastCalc != null, lastCalc, stale, published);
            }
            catch (Exception ex)
            {
                // Eksik tablo/geçici DB hatasında panoyu çökertme; uyarı gösterme.
                _logger.LogWarning(ex, "GetCalcFreshnessAsync okunamadı (year={Year} quarter={Quarter}); uyarı gizlendi.", year, quarter);
                return new CalcFreshness(true, null, false, true);
            }
        }

        // ── Hesaplama Ayarı: "Run-rate" modu (admin) ───────────────────────────
        // Açıkken hedef, sell-out verisi olan dönem sayısına orantılanır (3 aylık hedef vs
        // 2 aylık satış adaletsizliğini giderir). KAPALI (varsayılan) = bugünkü davranış.
        private const string RunRateModeKey = "RunRateMode";

        public async Task<bool> GetRunRateModeAsync()
        {
            try
            {
                using var db = _factory.CreateDbContext();
                var setting = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == RunRateModeKey);
                return setting?.Value == "true";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RunRateMode okunamadı (SystemSettings yok/erişilemez); mod kapalı varsayıldı.");
                return false;
            }
        }

        public async Task SetRunRateModeAsync(bool enabled, int userId)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == RunRateModeKey);
            var val = enabled ? "true" : "false";
            if (setting == null)
                db.SystemSettings.Add(new SystemSetting { Key = RunRateModeKey, Value = val, UpdatedAt = DateTime.UtcNow, UpdatedBy = userId });
            else { setting.Value = val; setting.UpdatedAt = DateTime.UtcNow; setting.UpdatedBy = userId; }
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "SetRunRateMode", "SystemSetting", null, newValue: $"RunRateMode = {val}");
        }

        public async Task<CalcResult> CalculateQuarterAsync(int year, string quarter, int calculatedBy)
        {
            ValidateQuarter(quarter);
            await EnsureAdminAsync(calculatedBy);
            using var db = _factory.CreateDbContext();
            var result = new CalcResult { Year = year, Quarter = quarter };

            try
            {

            var months = quarter switch
            {
                "Q1" => new[] { 1, 2, 3 },
                "Q2" => new[] { 4, 5, 6 },
                "Q3" => new[] { 7, 8, 9 },
                "Q4" => new[] { 10, 11, 12 },
                _ => Array.Empty<int>()
            };

            // Period ID'leri
            var periodIds = await db.Periods
                .Where(p => p.Year == year && months.Contains(p.Month))
                .Select(p => p.PeriodId).ToListAsync();

            if (!periodIds.Any()) { result.Error = "Dönem bulunamadı"; return result; }

            // Run-rate modu: hedef, sell-out verisi olan dönem sayısına orantılanır
            // (3 aylık hedef vs 2 aylık satış adaletsizliğini giderir). KAPALI = ham hedef.
            bool runRate = await GetRunRateModeAsync();
            int soldPeriods = await db.SellOuts
                .Where(s => periodIds.Contains(s.PeriodId) && s.Quantity != 0)
                .Select(s => s.PeriodId).Distinct().CountAsync();
            decimal soldRatio = ScoreCalculator.SoldPeriodRatio(soldPeriods, periodIds.Count);
            result.RunRateMode = runRate;
            result.SoldPeriods = soldPeriods;
            result.TotalPeriods = periodIds.Count;

            // ST'ler
            var stUsers = await db.Users
                .Include(u => u.Region)
                .Where(u => u.RoleId == RoleId.ST && u.IsActive && !u.IsDeleted)
                .ToListAsync();

            // Quarter başlangıç tarihi — geçmişe dönük ağırlık seçimi için
            var quarterStart = QuarterStart(year, quarter);

            // Ağırlıklar — tüm versiyonları çek (ValidTo olan geçmiş kayıtlar dahil)
            var allWeights = await db.ScorecardWeights
                .Include(w => w.Product)
                .Where(w => w.Product!.IsPrimEligible && w.Product.IsActive)
                .ToListAsync();

            // O quarter'da geçerli olan en son ağırlık (ValidFrom <= quarterStart).
            // Eşit ValidFrom'da (çift aktif satır) DETERMİNİSTİK: en son oluşturulan (max WeightId) kazanır.
            var weights = allWeights
                .Where(w => w.ValidFrom <= quarterStart)
                .GroupBy(w => w.ProductId)
                .Select(g => g.OrderByDescending(w => w.ValidFrom).ThenByDescending(w => w.WeightId).First())
                .ToList();

            // Hiç tarihsel kayıt yoksa (ağırlıklar sonradan girilmiş) → mevcut aktif scorecard
            if (!weights.Any())
            {
                weights = allWeights
                    .Where(w => w.ValidTo == null)
                    .GroupBy(w => w.ProductId)
                    .Select(g => g.OrderByDescending(w => w.ValidFrom).ThenByDescending(w => w.WeightId).First())
                    .ToList();
            }

            if (!weights.Any()) { result.Error = "Scorecard ağırlıkları tanımlı değil"; return result; }

            // Hedefler — parametrik raw SQL (güvenli + hızlı)
            var targetDict = new Dictionary<string, decimal>();
            var sellOutDict = new Dictionary<string, decimal>();
            var orderDict = new Dictionary<int, (decimal Total, int InstitutionCount)>();
            var brickSalesDict = new Dictionary<int, int>(); // UserId → distinct brick count with sales

            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            try
            {
                // Targets — sell-out ile AYNI atıf yolu: BrickId → UserBricks (canlı brick-ST
                // haritası). Targets.UserId (Hedef dosyasının Mümessil kolonu) yerine UserBricks
                // kullanılır; böylece hedef ve gerçekleşen daima aynı brick kümesinden hesaplanır
                // ve "Hedef vs Gerçekleşen farklı brick haritası" çakışması ortadan kalkar.
                {
                    var pidParams = string.Join(",", periodIds.Select((id, i) => $"@p{i}"));
                    // Ortak brick: hedef sahibin payıyla çarpılır (SharePercent=100 → değişmez).
                    var sql = $@"SELECT ub.UserId, t.ProductId, SUM(t.TargetQty * ub.SharePercent / 100.0) AS Total
                        FROM Targets t
                        INNER JOIN UserBricks ub ON t.BrickId = ub.BrickId AND ub.IsActive = 1
                        WHERE t.PeriodId IN ({pidParams})
                        GROUP BY ub.UserId, t.ProductId";
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 120;
                    for (int i = 0; i < periodIds.Count; i++)
                    {
                        var param = cmd.CreateParameter();
                        param.ParameterName = $"@p{i}";
                        param.Value = periodIds[i];
                        cmd.Parameters.Add(param);
                    }
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var uid = reader.GetInt32(0);
                        var pid = reader.GetInt32(1);
                        var total = reader.GetDecimal(2);
                        targetDict[$"{uid}_{pid}"] = total;
                    }
                }

                // SellOut — live UserBricks JOIN for current brick-ST assignments
                {
                    var pidParams = string.Join(",", periodIds.Select((id, i) => $"@p{i}"));
                    // Ortak brick: sell-out sahibin payıyla çarpılır (SharePercent=100 → değişmez).
                    var sql = $@"SELECT ub.UserId, so.ProductId, SUM(so.Quantity * ub.SharePercent / 100.0) AS Total
                        FROM SellOuts so
                        INNER JOIN UserBricks ub ON so.BrickId = ub.BrickId AND ub.IsActive = 1
                        WHERE so.PeriodId IN ({pidParams}) AND so.Quantity > 0
                        GROUP BY ub.UserId, so.ProductId";
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 120;
                    for (int i = 0; i < periodIds.Count; i++)
                    {
                        var param = cmd.CreateParameter();
                        param.ParameterName = $"@p{i}";
                        param.Value = periodIds[i];
                        cmd.Parameters.Add(param);
                    }
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var uid = reader.GetInt32(0);
                        var pid = reader.GetInt32(1);
                        var total = reader.GetDecimal(2);
                        var key = $"{uid}_{pid}";
                        sellOutDict[key] = sellOutDict.TryGetValue(key, out var existing) ? existing + total : total;
                    }
                }

                // BrickSales — distinct bricks with sell-out per user (live UserBricks JOIN)
                {
                    var pidParams = string.Join(",", periodIds.Select((id, i) => $"@p{i}"));
                    var sql = $@"SELECT ub.UserId, COUNT(DISTINCT so.BrickId) AS BrickCount
                        FROM SellOuts so
                        INNER JOIN UserBricks ub ON so.BrickId = ub.BrickId AND ub.IsActive = 1
                        WHERE so.PeriodId IN ({pidParams}) AND so.Quantity > 0
                        GROUP BY ub.UserId";
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 120;
                    for (int i = 0; i < periodIds.Count; i++)
                    {
                        var param = cmd.CreateParameter();
                        param.ParameterName = $"@p{i}";
                        param.Value = periodIds[i];
                        cmd.Parameters.Add(param);
                    }
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var uid = reader.GetInt32(0);
                        var cnt = reader.GetInt32(1);
                        brickSalesDict[uid] = cnt;
                    }
                }

                // Orders — UserId direkt yoksa UserBricks JOIN ile BrickId'den türetilir
                {
                    var pidParams = string.Join(",", periodIds.Select((id, i) => $"@p{i}"));
                    // Ortak brick: ub JOIN'i yalnız sipariş kendi UserId'sini taşımıyorsa devreye
                    // girer (o.UserId IS NULL) — aksi halde satır çoğalıp SUM'ı iki katına çıkarırdı.
                    // Brick-türevli siparişte kutu miktarı sahibin payıyla çarpılır (100 → değişmez).
                    var sql = $@"SELECT COALESCE(o.UserId, ub.UserId) AS UserId,
                            SUM(o.BoxQuantity * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS Total,
                            SUM(ISNULL(o.InstitutionCount,0)) AS InstCount
                        FROM Orders o
                        LEFT JOIN UserBricks ub ON o.UserId IS NULL AND o.BrickId = ub.BrickId AND ub.IsActive = 1
                        WHERE o.PeriodId IN ({pidParams})
                          AND COALESCE(o.UserId, ub.UserId) IS NOT NULL
                        GROUP BY COALESCE(o.UserId, ub.UserId)";
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 120;
                    for (int i = 0; i < periodIds.Count; i++)
                    {
                        var param = cmd.CreateParameter();
                        param.ParameterName = $"@p{i}";
                        param.Value = periodIds[i];
                        cmd.Parameters.Add(param);
                    }
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var uid = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                        if (uid == 0) continue;
                        var total = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
                        var instCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                        if (orderDict.TryGetValue(uid, out var existing))
                            orderDict[uid] = (existing.Total + total, existing.InstitutionCount + instCount);
                        else
                            orderDict[uid] = (total, instCount);
                    }
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            // Ruta — aynı user için 2+ kayıt varsa ilkini al (duplicate-key crash önleme)
            var rutaDict = (await db.RutaUyum
                .Where(r => r.Year == year && r.Quarter == quarter)
                .ToListAsync())
                .GroupBy(r => r.UserId)
                .ToDictionary(g => g.Key, g => g.First());

            // Strike Rate — ziyaret planı GLN'leri (ST bazında unique)
            var visitPlanGLNs = (await db.VisitPlans
                .Where(v => v.Year == year && v.Quarter == quarter && v.UserId != null)
                .ToListAsync())
                .GroupBy(v => v.UserId!.Value)
                .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(x => x.GLN)));

            // Strike Rate — sipariş GLN'leri (UserId + quarter bazında unique)
            var orderGLNs = (await db.Orders
                .Where(o => periodIds.Contains(o.PeriodId) && o.GLN != null && o.UserId != null)
                .ToListAsync())
                .GroupBy(o => o.UserId!.Value)
                .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(x => x.GLN!)));

            // Brick atama sayıları — distScore için
            var userBrickCounts = await db.UserBricks
                .Where(ub => ub.IsActive)
                .GroupBy(ub => ub.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.UserId, x => x.Count);

            // Config snapshot
            var kpiList = await db.KPICriteria.Where(k => k.Year == year && k.ValidTo == null).ToListAsync();

            // KPI ağırlıkları — Name bazlı erişim. Aynı ad için çift aktif kayıt bulunabildiğinden
            // seçim PickKpi ile deterministik (en son oluşturulan kazanır), aksi halde hangi kopyanın
            // kullanıldığı belirsiz kalırdı.
            var kpiSatis = PickKpi(kpiList, "Satış", "Sales");
            var kpiRuta = PickKpi(kpiList, "Ruta", "Route");
            var kpiDist = PickKpi(kpiList, "Dağıtım", "Dist");
            var kpiOrder = PickKpi(kpiList, "Sipariş", "Order");

            // KPI ağırlık yüzdeleri (varsayılan: Satış 60, Dağıtım 15, Ruta 15, Sipariş 10)
            decimal kpiSatisW = (kpiSatis?.Weight ?? 60) / 100m;
            decimal kpiRutaW = (kpiRuta?.Weight ?? 15) / 100m;
            decimal kpiDistW = (kpiDist?.Weight ?? 15) / 100m;
            decimal kpiOrderW = (kpiOrder?.Weight ?? 10) / 100m;
            // Prim kademe tablosu + rol bazlı taban tutar
            var primTiers = (await db.PrimTiers.Where(t => t.IsActive).ToListAsync())
                .Select(t => (t.MinScore, t.MaxScore, t.Multiplier)).ToList();
            var roleBaseDict = await db.RolePrimBases.ToDictionaryAsync(r => r.RoleId, r => r.BaseAmount);

            var configSnapshot = JsonSerializer.Serialize(new
            {
                Weights = weights.Select(w => new { w.Product!.Name, w.Weight }),
                KPI = kpiList.Select(k => new { k.Name, k.Weight, k.MinRate }),
                PrimTiers = primTiers.Select(t => new { t.MinScore, t.MaxScore, t.Multiplier }),
                RoleBase = roleBaseDict
            });

            // Hesapla — tamamen bellekte (transaction başlamadan önce)
            var allCalcs = new List<Calculation>();
            var allDetailPairs = new List<(Calculation Calc, List<CalculationDetail> Details)>();

            foreach (var st in stUsers)
            {
                int monthsWorked = ScoreCalculator.MonthsWorked(st.HireDate, quarterStart);
                bool isEligible = monthsWorked >= st.MinMonths;

                decimal totalScore = 0;
                var details = new List<CalculationDetail>();
                int productsWithTarget = 0;
                int productsWithSellOut = 0;

                foreach (var w in weights)
                {
                    var tKey = $"{st.UserId}_{w.ProductId}";
                    var rawTarget = targetDict.TryGetValue(tKey, out var t) ? t : 0;
                    var actualQty = sellOutDict.TryGetValue(tKey, out var s) ? s : 0;
                    // Run-rate açıksa hedef, satış verisi olan dönem oranıyla küçültülür.
                    var targetQty = runRate ? ScoreCalculator.ProrateTarget(rawTarget, soldRatio) : rawTarget;
                    if (rawTarget > 0) productsWithTarget++;
                    if (actualQty > 0) productsWithSellOut++;

                    var ps = ScoreCalculator.ScoreProduct(targetQty, actualQty, w.Weight);
                    totalScore += ps.Score;

                    details.Add(new CalculationDetail
                    {
                        ProductId = w.ProductId,
                        TargetQty = targetQty,
                        ActualQty = actualQty,
                        Rate = ps.Rate,
                        CappedRate = ps.CappedRate,
                        Score = ps.Score,
                        IsCapped = ps.IsCapped
                    });
                }

                // === KPI SKORLARI ===

                // 1. Satış Hacmi skoru — ürün ağırlıklarından gelen oran (0-1+ arası, cap'li)
                //    totalScore zaten 0-1 arasında normalize (ağırlıklar /100 ile çarpılmış)
                decimal salesScore = totalScore; // Ürün bazlı toplam skor (ör: 0.92)

                // 2. Ruta Uyum skoru (0-1 arası, veri yoksa null)
                decimal? rutaRate = rutaDict.TryGetValue(st.UserId, out var ruta) ? ruta.Rate : null;
                decimal rutaScore = rutaRate ?? 0m;

                // 3. Dağıtım skoru — satış yapılan brick / ST'ye atanmış toplam brick (cap %100)
                var stTotalBricks = userBrickCounts.TryGetValue(st.UserId, out var bc) ? bc : 0;
                var stSoldBricks = brickSalesDict.TryGetValue(st.UserId, out var sb) ? sb : 0;
                decimal distScore = ScoreCalculator.DistributionScore(stSoldBricks, stTotalBricks);

                // 4. Strike Rate — ziyaret planındaki GLN'lerden kaçına sipariş yapıldı
                //    Ziyaret planı yoksa null kalır → UI'de "—" gösterilir (hesaba dahil edilmez)
                decimal? orderScore = null;
                if (visitPlanGLNs.TryGetValue(st.UserId, out var plannedGLNSet) && plannedGLNSet.Count > 0)
                {
                    int matched = orderGLNs.TryGetValue(st.UserId, out var orderedGLNSet)
                        ? orderedGLNSet.Count(g => plannedGLNSet.Contains(g))
                        : 0;
                    orderScore = ScoreCalculator.StrikeScore(plannedGLNSet.Count, matched);
                }

                // === FİNAL SKOR — 4 KPI ağırlıklı ===
                // orderScore null ise hesaba 0 olarak girer ama UI'de "—" görünür
                decimal finalScore = ScoreCalculator.FinalScore(
                    salesScore, rutaScore, distScore, orderScore,
                    kpiSatisW, kpiRutaW, kpiDistW, kpiOrderW);

                // Gate kontrolü — %85 eşiği FinalScore üzerinden
                bool passedGate = ScoreCalculator.PassedGate(finalScore);

                // Prim tutarı — rol bazlı taban × kademe çarpanı (hak ediş yoksa 0)
                decimal stBase = roleBaseDict.TryGetValue(st.RoleId, out var baseAmt) ? baseAmt : 0m;
                decimal primAmount = ScoreCalculator.PrimAmount(finalScore, stBase, isEligible, passedGate, primTiers);

                var calc = new Calculation
                {
                    UserId = st.UserId,
                    Year = year,
                    Quarter = quarter,
                    TotalScore = salesScore,       // Satış hacmi skoru (geriye uyumluluk)
                    PassedGate = passedGate,
                    IsEligible = isEligible,
                    IneligibleReason = isEligible ? null : $"Yeni Başlayan ({monthsWorked}/{st.MinMonths} ay)",
                    RutaScore = rutaRate,
                    DistScore = distScore,
                    OrderScore = orderScore,
                    StrikeScore = orderScore,
                    FinalScore = finalScore,
                    PrimAmount = primAmount,
                    IsFrozen = true,
                    ConfigSnapshot = configSnapshot,
                    CalculatedAt = DateTime.UtcNow,
                    CalculatedBy = calculatedBy
                };

                allCalcs.Add(calc);
                allDetailPairs.Add((calc, details));

                result.STResults.Add(new STCalcResult
                {
                    UserId = st.UserId,
                    FullName = st.FullName,
                    TotalScore = salesScore,
                    FinalScore = finalScore,
                    PassedGate = calc.PassedGate,
                    IsEligible = isEligible,
                    IneligibleReason = calc.IneligibleReason,
                    RutaScore = rutaRate,
                    DistScore = distScore,
                    OrderScore = orderScore ?? 0m,
                    PrimAmount = primAmount,
                    ProductsWithTarget = productsWithTarget,
                    ProductsWithSellOut = productsWithSellOut
                });
            }

            // Üzerine yazılan önceki hesaplamanın özeti — denetim kaydı için. Bu çeyrek şu an
            // versiyonlanmıyor, silinip yeniden yazılıyor; en azından NE ezildiği audit'e düşsün
            // ("kim, ne zaman, hangi sonucu ezdi" sorusu cevaplanabilsin).
            string? oncekiOzet = null;

            // Atomik kayıt: sil + ekle tek transaction'da
            using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                var oldCalcs = await db.Calculations
                    .Where(c => c.Year == year && c.Quarter == quarter).ToListAsync();
                if (oldCalcs.Any())
                {
                    oncekiOzet = $"{oldCalcs.Count} ST · {oldCalcs.Count(c => c.IsPublished)} yayında · " +
                                 $"{oldCalcs.Count(c => c.PassedGate)} gate geçti · " +
                                 $"toplam {oldCalcs.Sum(c => c.PrimAmount ?? 0m):N2} TL";
                    var oldIds = oldCalcs.Select(c => c.CalcId).ToList();
                    var oldDetails = await db.CalculationDetails
                        .Where(d => oldIds.Contains(d.CalcId)).ToListAsync();
                    db.CalculationDetails.RemoveRange(oldDetails);
                    db.Calculations.RemoveRange(oldCalcs);
                    await db.SaveChangesAsync();
                }

                db.Calculations.AddRange(allCalcs);
                await db.SaveChangesAsync(); // CalcId'leri alır

                var allDetails = new List<CalculationDetail>();
                foreach (var (calc, details) in allDetailPairs)
                {
                    foreach (var d in details) d.CalcId = calc.CalcId;
                    allDetails.AddRange(details);
                }
                db.CalculationDetails.AddRange(allDetails);
                await db.SaveChangesAsync();

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }

            result.Success = true;
            result.STCount = stUsers.Count;
            result.PassedCount = result.STResults.Count(r => r.PassedGate && r.IsEligible);

            // Prim tutarını değiştiren EN kritik işlem — audit'e yazılmadan geçilmemeli.
            // oncekiOzet doluysa yayınlanmış/hesaplanmış bir sonuç ÜZERİNE yazıldı demektir.
            await _audit.LogAsync(calculatedBy, "Calculate", "Calculation", null,
                oldValue: oncekiOzet,
                newValue: $"{year} {quarter}: {result.STCount} ST · {result.PassedCount} gate geçti · " +
                          $"toplam {allCalcs.Sum(c => c.PrimAmount ?? 0m):N2} TL" +
                          (runRate ? $" · run-rate {soldPeriods}/{periodIds.Count}" : ""));

            }
            catch (Exception ex)
            {
                var inner = ex.InnerException?.InnerException?.Message
                         ?? ex.InnerException?.Message
                         ?? "";
                var msg = string.IsNullOrEmpty(inner) ? ex.Message : inner;
                result.Error = $"Hesaplama hatası: {msg}";
            }

            return result;
        }

        public async Task<DataCheckResult> CheckDataAsync(int year, string quarter)
        {
            using var db = _factory.CreateDbContext();
            var months = quarter switch
            {
                "Q1" => new[] { 1, 2, 3 },
                "Q2" => new[] { 4, 5, 6 },
                "Q3" => new[] { 7, 8, 9 },
                "Q4" => new[] { 10, 11, 12 },
                _ => Array.Empty<int>()
            };

            var periodIds = await db.Periods
                .Where(p => p.Year == year && months.Contains(p.Month))
                .Select(p => p.PeriodId).ToListAsync();

            return new DataCheckResult
            {
                PeriodCount     = periodIds.Count,
                TargetCount     = await db.Targets.CountAsync(t => periodIds.Contains(t.PeriodId)),
                SellOutCount    = await db.SellOuts.CountAsync(s => periodIds.Contains(s.PeriodId) && s.Quantity > 0),
                UserBricksCount = await db.UserBricks.CountAsync(ub => ub.IsActive),
                // Ürün sayısı — satır sayısı DEĞİL: bir üründe çift aktif satır olabildiğinden
                // ham COUNT şişik görünüyordu ("13 ağırlık" yerine 25).
                WeightsCount    = await db.ScorecardWeights
                                    .Where(w => w.ValidTo == null && w.Product!.IsActive && w.Product.IsPrimEligible)
                                    .Select(w => w.ProductId).Distinct().CountAsync(),
                STCount         = await db.Users.CountAsync(u => u.RoleId == RoleId.ST && u.IsActive && !u.IsDeleted)
            };
        }

        public async Task PublishAsync(int year, string quarter, int publishedBy)
        {
            ValidateQuarter(quarter);
            await EnsureAdminAsync(publishedBy);
            using var db = _factory.CreateDbContext();
            var calcs = await db.Calculations
                .Where(c => c.Year == year && c.Quarter == quarter).ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var c in calcs) { c.IsPublished = true; c.PublishedAt = now; c.PublishedBy = publishedBy; c.IsPaused = false; c.PausedAt = null; c.PausedBy = null; }
            await db.SaveChangesAsync();
            await _audit.LogAsync(publishedBy, "Publish", "Calculation", null,
                newValue: $"{year} {quarter} — {calcs.Count} hesaplama yayınlandı");
            try
            {
                await _notifications.SendToRoleAsync(
                    RoleId.ST,
                    $"{year} {quarter} Prim Sonuçları Yayınlandı",
                    $"{year} yılı {quarter} dönemi prim hesaplama sonuçlarınız hazır. Panoyu ziyaret ederek sonuçlarınızı görüntüleyebilirsiniz.",
                    "CalculationPublished",
                    publishedBy);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Yayın bildirimi gönderilemedi ({Year} {Quarter})", year, quarter);
            }
        }

        public async Task UnpublishAsync(int year, string quarter, int stoppedBy = 0)
        {
            ValidateQuarter(quarter);
            if (stoppedBy > 0) await EnsureAdminAsync(stoppedBy);
            using var db = _factory.CreateDbContext();
            var calcs = await db.Calculations
                .Where(c => c.Year == year && c.Quarter == quarter).ToListAsync();
            foreach (var c in calcs) { c.IsPublished = false; c.PublishedAt = null; c.PublishedBy = null; }
            await db.SaveChangesAsync();
            if (stoppedBy > 0)
                await _audit.LogAsync(stoppedBy, "Unpublish", "Calculation", null,
                    oldValue: $"{year} {quarter} — {calcs.Count} hesaplama yayından kaldırıldı");
        }

        public async Task<(int Year, string Quarter)?> GetLatestPublishedAsync()
        {
            using var db = _factory.CreateDbContext();
            var latest = await db.Calculations
                .Where(c => c.IsPublished && !c.IsPaused)
                .GroupBy(c => new { c.Year, c.Quarter })
                .Select(g => new { g.Key.Year, g.Key.Quarter })
                .OrderByDescending(x => x.Year).ThenByDescending(x => x.Quarter)
                .FirstOrDefaultAsync();
            return latest == null ? null : (latest.Year, latest.Quarter);
        }

        /// <summary>
        /// Yayın durumuna bakmaksızın en son hesaplanan dönem — admin panosu, yayın
        /// durdurulduğunda/kaldırıldığında bile taslak görünümle çalışabilsin diye.
        /// </summary>
        public async Task<(int Year, string Quarter)?> GetLatestCalculatedAsync()
        {
            using var db = _factory.CreateDbContext();
            var latest = await db.Calculations
                .GroupBy(c => new { c.Year, c.Quarter })
                .Select(g => new { g.Key.Year, g.Key.Quarter })
                .OrderByDescending(x => x.Year).ThenByDescending(x => x.Quarter)
                .FirstOrDefaultAsync();
            return latest == null ? null : (latest.Year, latest.Quarter);
        }

        public record StarBadge(string FullName, string Region, decimal Value, string Period, string MetricLabel = "Kutu");

        // ── Sipariş Leaderboard ───────────────────────────────────────────────

        public async Task<List<OrderLeaderRow>> GetOrderLeaderboardAsync(
            int? productId = null, int? year = null, string? quarter = null)
        {
            using var db = _factory.CreateDbContext();
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            var rows = new List<OrderLeaderRow>();
            try
            {
                // Dönem filtresi için PeriodId listesi
                var periodFilter = "";
                var periodParams = new List<(string name, object val)>();

                if (year.HasValue && !string.IsNullOrEmpty(quarter))
                {
                    var months = quarter switch
                    {
                        "Q1" => new[] { 1, 2, 3 }, "Q2" => new[] { 4, 5, 6 },
                        "Q3" => new[] { 7, 8, 9 },  "Q4" => new[] { 10, 11, 12 },
                        _ => Array.Empty<int>()
                    };
                    var periodIds = await db.Periods
                        .Where(p => p.Year == year.Value && months.Contains(p.Month))
                        .Select(p => p.PeriodId).ToListAsync();
                    if (periodIds.Any())
                    {
                        var pp = string.Join(",", periodIds.Select((id, i) => { periodParams.Add(($"@pid{i}", id)); return $"@pid{i}"; }));
                        periodFilter = $"AND o.PeriodId IN ({pp})";
                    }
                }

                var productFilter = "";
                if (productId.HasValue)
                {
                    productFilter = "AND o.ProductId = @prodId";
                    periodParams.Add(("@prodId", productId.Value));
                }

                // Ortak brick: ub JOIN yalnız o.UserId NULL iken (çift-sayım guard'ı) + pay çarpanı.
                var sql = $@"
                    SELECT
                        COALESCE(o.UserId, ub.UserId) AS uid,
                        u.FullName,
                        ISNULL(r.Name,'') AS Region,
                        SUM(o.BoxQuantity * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS TotalBoxes,
                        SUM(ISNULL(o.AmountTL,0) * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS TotalTL,
                        SUM(ISNULL(o.InstitutionCount, 0)) AS UniquePharmacies
                    FROM Orders o
                    LEFT JOIN UserBricks ub ON o.UserId IS NULL AND o.BrickId = ub.BrickId AND ub.IsActive = 1
                    INNER JOIN Users u ON COALESCE(o.UserId, ub.UserId) = u.UserId
                    LEFT JOIN Regions r ON u.RegionId = r.RegionId
                    WHERE u.IsActive = 1 AND u.IsDeleted = 0
                      AND COALESCE(o.UserId, ub.UserId) IS NOT NULL
                      {periodFilter} {productFilter}
                    GROUP BY COALESCE(o.UserId, ub.UserId), u.FullName, r.Name
                    ORDER BY TotalBoxes DESC";

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 60;
                foreach (var (pname, pval) in periodParams)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = pname; p.Value = pval;
                    cmd.Parameters.Add(p);
                }

                using var reader = await cmd.ExecuteReaderAsync();
                int rank = 1;
                while (await reader.ReadAsync())
                {
                    var uid = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
                    rows.Add(new OrderLeaderRow(
                        rank++,
                        uid,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? 0 : reader.GetDecimal(3),
                        reader.IsDBNull(4) ? 0 : reader.GetDecimal(4),
                        reader.IsDBNull(5) ? 0 : reader.GetInt32(5)));
                }
            }
            finally { await db.Database.CloseConnectionAsync(); }
            return rows;
        }

        public async Task<OrderLeaderboardPublish?> GetActiveOrderLeaderboardAsync(bool includePaused = false)
        {
            using var db = _factory.CreateDbContext();
            var q = db.OrderLeaderboardPublishes
                .Include(p => p.Product)
                .Where(p => p.IsActive);
            if (!includePaused) q = q.Where(p => !p.IsPaused);
            return await q.OrderByDescending(p => p.PublishedAt).FirstOrDefaultAsync();
        }

        public async Task PublishOrderLeaderboardAsync(int? productId, int? year, string? quarter, int publishedBy)
        {
            if (!string.IsNullOrEmpty(quarter)) ValidateQuarter(quarter);
            await EnsureAdminAsync(publishedBy);
            using var db = _factory.CreateDbContext();
            // Aktif yayını durdur
            var active = await db.OrderLeaderboardPublishes.Where(p => p.IsActive).ToListAsync();
            foreach (var a in active) { a.IsActive = false; a.StoppedAt = DateTime.UtcNow; a.StoppedBy = publishedBy; }
            // Yeni yayın
            db.OrderLeaderboardPublishes.Add(new OrderLeaderboardPublish
            {
                ProductId = productId,
                Year = year,
                Quarter = quarter,
                PublishedBy = publishedBy,
                PublishedAt = DateTime.UtcNow,
                IsActive = true
            });
            await db.SaveChangesAsync();
            await _audit.LogAsync(publishedBy, "PublishOrderLeaderboard", "OrderLeaderboard", null,
                newValue: $"{year} {quarter} aktif edildi");
        }

        public async Task StopOrderLeaderboardAsync(int stoppedBy)
        {
            await EnsureAdminAsync(stoppedBy);
            using var db = _factory.CreateDbContext();
            var active = await db.OrderLeaderboardPublishes.Where(p => p.IsActive).ToListAsync();
            foreach (var a in active) { a.IsActive = false; a.StoppedAt = DateTime.UtcNow; a.StoppedBy = stoppedBy; }
            await db.SaveChangesAsync();
            await _audit.LogAsync(stoppedBy, "StopOrderLeaderboard", "OrderLeaderboard", null,
                oldValue: $"{active.Count} aktif sıralama durduruldu");
        }

        // ── Beklet / Devam Et ─────────────────────────────────────────────────

        public async Task PauseAsync(int year, string quarter, int userId)
        {
            ValidateQuarter(quarter);
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var calcs = await db.Calculations
                .Where(c => c.Year == year && c.Quarter == quarter && c.IsPublished && !c.IsPaused)
                .ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var c in calcs) { c.IsPaused = true; c.PausedAt = now; c.PausedBy = userId; }
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "PauseCalculation", "Calculation", null,
                newValue: $"{year} {quarter} — {calcs.Count} hesaplama bekletildi");
        }

        public async Task ResumeAsync(int year, string quarter, int userId)
        {
            ValidateQuarter(quarter);
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var calcs = await db.Calculations
                .Where(c => c.Year == year && c.Quarter == quarter && c.IsPublished && c.IsPaused)
                .ToListAsync();
            foreach (var c in calcs) { c.IsPaused = false; c.PausedAt = null; c.PausedBy = null; }
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "ResumeCalculation", "Calculation", null,
                newValue: $"{year} {quarter} — {calcs.Count} hesaplama yeniden aktif");
        }

        public async Task PauseOrderLeaderboardAsync(int userId)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var active = await db.OrderLeaderboardPublishes
                .Where(p => p.IsActive && !p.IsPaused).ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var a in active) { a.IsPaused = true; a.PausedAt = now; a.PausedBy = userId; }
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "PauseOrderLeaderboard", "OrderLeaderboard", null,
                newValue: $"{active.Count} sıralama bekletildi");
        }

        public async Task ResumeOrderLeaderboardAsync(int userId)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var paused = await db.OrderLeaderboardPublishes
                .Where(p => p.IsActive && p.IsPaused).ToListAsync();
            foreach (var p in paused) { p.IsPaused = false; p.PausedAt = null; p.PausedBy = null; }
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "ResumeOrderLeaderboard", "OrderLeaderboard", null,
                newValue: $"{paused.Count} sıralama yeniden aktif");
        }

        public async Task<bool> IsCalculationPausedAsync(int year, string quarter)
        {
            using var db = _factory.CreateDbContext();
            return await db.Calculations.AnyAsync(c => c.Year == year && c.Quarter == quarter && c.IsPublished && c.IsPaused);
        }

        // ── Bölge Analitik ────────────────────────────────────────────────────

        public async Task<List<RegionAnalyticsRow>> GetRegionAnalyticsAsync(int year, string quarter)
        {
            using var db = _factory.CreateDbContext();
            var summaries = await GetResultsAsync(year, quarter);
            return summaries
                .Where(s => s.Calc.User?.Region != null)
                .GroupBy(s => s.Calc.User!.Region!.Name)
                .Select(g =>
                {
                    var eligible = g.Where(x => x.Calc.IsEligible).ToList();
                    return new RegionAnalyticsRow
                    {
                        RegionName = g.Key,
                        StCount = g.Count(),
                        PassedCount = eligible.Count(x => x.Calc.PassedGate),
                        PassRate = eligible.Any() ? (double)eligible.Count(x => x.Calc.PassedGate) / eligible.Count : 0,
                        AvgFinalScore = g.Average(x => (double)(x.Calc.FinalScore ?? x.Calc.TotalScore)),
                        AvgSalesScore = g.Average(x => (double)x.Calc.TotalScore),
                        AvgRutaScore = g.Average(x => (double)(x.Calc.RutaScore ?? 0)),
                        AvgDistScore = g.Average(x => (double)(x.Calc.DistScore ?? 0)),
                        AvgStrikeScore = g.Average(x => (double)(x.Calc.StrikeScore ?? 0))
                    };
                })
                .OrderByDescending(r => r.PassRate)
                .ToList();
        }

        private static string MetricToSql(string metric) => metric switch
        {
            "InstitutionCount" => "SUM(ISNULL(o.InstitutionCount,0)) DESC",
            "AmountTL"         => "SUM(o.AmountTL) DESC",
            "MFTL"             => "SUM(o.MFTL) DESC",
            _                  => "SUM(o.BoxQuantity) DESC"
        };

        private static int MetricToValueCol(string metric) => metric switch
        {
            "InstitutionCount" => 3,
            "AmountTL"         => 4,
            "MFTL"             => 5,
            _                  => 2
        };

        public async Task<(StarBadge? VolumeStar, StarBadge? ReachStar)> GetOrderBadgesAsync()
        {
            using var db = _factory.CreateDbContext();
            try
            {
                var latestPeriod = await db.Periods
                    .Where(p => db.Orders.Any(o => o.PeriodId == p.PeriodId))
                    .OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
                    .Select(p => new { p.PeriodId, p.Year, p.Month })
                    .FirstOrDefaultAsync();
                if (latestPeriod == null) return (null, null);

                // Konfigüre edilmiş metrikler (default: hafta=hacim, ay=erişim)
                string weekMetric = "BoxQuantity", monthMetric = "InstitutionCount";
                try
                {
                    var criteria = await db.StarCriteria.ToListAsync();
                    weekMetric  = criteria.FirstOrDefault(c => c.StarKey == "week")?.Metric  ?? weekMetric;
                    monthMetric = criteria.FirstOrDefault(c => c.StarKey == "month")?.Metric ?? monthMetric;
                }
                catch { /* StarCriteria tablosu henüz oluşturulmadı; default metrik kullan */ }

                var label = $"{latestPeriod.Year}/{latestPeriod.Month:D2}";
                var conn = db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync();
                try
                {
                    // SELECT'te tüm olası metrikler çekilir; sıralama ORDER BY'dan gelir
                    // Ortak brick: ub JOIN yalnız o.UserId NULL iken (çift-sayım guard'ı) + pay çarpanı.
                    var baseSql = @"SELECT TOP 1 u.FullName, ISNULL(r.Name,'') AS Reg,
                        SUM(o.BoxQuantity * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS Vol,
                        SUM(ISNULL(o.InstitutionCount,0)) AS Reach,
                        SUM(o.AmountTL * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS AmtTL,
                        SUM(o.MFTL * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS MfTL
                        FROM Orders o
                        LEFT JOIN UserBricks ub ON o.UserId IS NULL AND o.BrickId = ub.BrickId AND ub.IsActive = 1
                        LEFT JOIN Users u ON COALESCE(o.UserId, ub.UserId) = u.UserId
                        LEFT JOIN Regions r ON u.RegionId = r.RegionId
                        WHERE o.PeriodId = @pid AND COALESCE(o.UserId, ub.UserId) IS NOT NULL
                          AND u.IsActive = 1 AND u.IsDeleted = 0
                        GROUP BY u.FullName, r.Name ORDER BY ";

                    StarBadge? vol = null, reach = null;
                    foreach (var (metric, isWeek) in new[] { (weekMetric, true), (monthMetric, false) })
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = baseSql + MetricToSql(metric);
                        cmd.CommandTimeout = 30;
                        var prm = cmd.CreateParameter();
                        prm.ParameterName = "@pid"; prm.Value = latestPeriod.PeriodId;
                        cmd.Parameters.Add(prm);
                        using var r = await cmd.ExecuteReaderAsync();
                        if (await r.ReadAsync())
                        {
                            var colIdx = MetricToValueCol(metric);
                            // InstitutionCount sütunu SUM(int) → INT döner; GetDecimal InvalidCast atar.
                            // GetValue + Convert ile hem int hem decimal güvenle okunur.
                            var val = r.IsDBNull(colIdx) ? 0m : Convert.ToDecimal(r.GetValue(colIdx));
                            var metricLabel = metric switch
                            {
                                "InstitutionCount" => "Kurum",
                                "AmountTL"         => "Ana Mal TL",
                                "MFTL"             => "MF TL",
                                _                  => "Kutu"
                            };
                            var badge = new StarBadge(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), val, label, metricLabel);
                            if (isWeek) vol = badge; else reach = badge;
                        }
                    }
                    return (vol, reach);
                }
                finally { await db.Database.CloseConnectionAsync(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetOrderBadgesAsync başarısız");
                return (null, null);
            }
        }

        public async Task<Dictionary<int, (decimal BoxQty, decimal AmountTL, int InstCount)>> GetOrderSummariesAsync(int year, string quarter, int? productId = null)
        {
            using var db = _factory.CreateDbContext();
            var months = quarter switch
            {
                "Q1" => new[] { 1, 2, 3 }, "Q2" => new[] { 4, 5, 6 },
                "Q3" => new[] { 7, 8, 9 }, _ => new[] { 10, 11, 12 }
            };
            var periodIds = await db.Periods
                .Where(p => p.Year == year && months.Contains(p.Month))
                .Select(p => p.PeriodId).ToListAsync();
            if (!periodIds.Any()) return new();

            var result = new Dictionary<int, (decimal, decimal, int)>();
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            try
            {
                var pidParams = string.Join(",", periodIds.Select((_, i) => $"@p{i}"));
                var productFilter = productId.HasValue ? " AND o.ProductId = @prodId" : "";
                // Ortak brick: ub JOIN yalnız o.UserId NULL iken (çift-sayım guard'ı) + pay çarpanı.
                var sql = $@"SELECT COALESCE(o.UserId, ub.UserId) AS UserId,
                    SUM(o.BoxQuantity * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS BoxQty,
                    SUM(ISNULL(o.AmountTL,0) * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS AmtTL,
                    SUM(ISNULL(o.InstitutionCount,0)) AS InstCount
                    FROM Orders o
                    LEFT JOIN UserBricks ub ON o.UserId IS NULL AND o.BrickId = ub.BrickId AND ub.IsActive = 1
                    WHERE o.PeriodId IN ({pidParams}){productFilter} AND COALESCE(o.UserId, ub.UserId) IS NOT NULL
                    GROUP BY COALESCE(o.UserId, ub.UserId)";
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                for (int i = 0; i < periodIds.Count; i++)
                {
                    var prm = cmd.CreateParameter();
                    prm.ParameterName = $"@p{i}"; prm.Value = periodIds[i];
                    cmd.Parameters.Add(prm);
                }
                if (productId.HasValue)
                {
                    var prodPrm = cmd.CreateParameter();
                    prodPrm.ParameterName = "@prodId"; prodPrm.Value = productId.Value;
                    cmd.Parameters.Add(prodPrm);
                }
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var uid = r.IsDBNull(0) ? 0 : r.GetInt32(0);
                    if (uid == 0) continue;
                    result[uid] = (
                        r.IsDBNull(1) ? 0m : r.GetDecimal(1),
                        r.IsDBNull(2) ? 0m : r.GetDecimal(2),
                        r.IsDBNull(3) ? 0 : r.GetInt32(3)
                    );
                }
            }
            finally { await db.Database.CloseConnectionAsync(); }
            return result;
        }

        public async Task SaveStarCriteriaAsync(string weekMetric, string monthMetric, int updatedByUserId)
        {
            await EnsureAdminAsync(updatedByUserId);
            using var db = _factory.CreateDbContext();
            var all = await db.StarCriteria.ToListAsync();

            var weekLabels = new Dictionary<string, string>
            {
                ["BoxQuantity"]      = "Toplam Kutu",
                ["InstitutionCount"] = "Kurum Adedi",
                ["AmountTL"]         = "Ana Mal TL",
                ["MFTL"]             = "MF TL"
            };

            var weekCrit = all.FirstOrDefault(c => c.StarKey == "week");
            if (weekCrit == null) db.StarCriteria.Add(weekCrit = new() { StarKey = "week" });
            weekCrit.Metric = weekMetric;
            weekCrit.DisplayLabel = weekLabels.GetValueOrDefault(weekMetric, weekMetric);
            weekCrit.UpdatedAt = DateTime.UtcNow;
            weekCrit.UpdatedByUserId = updatedByUserId;

            var monthCrit = all.FirstOrDefault(c => c.StarKey == "month");
            if (monthCrit == null) db.StarCriteria.Add(monthCrit = new() { StarKey = "month" });
            monthCrit.Metric = monthMetric;
            monthCrit.DisplayLabel = weekLabels.GetValueOrDefault(monthMetric, monthMetric);
            monthCrit.UpdatedAt = DateTime.UtcNow;
            monthCrit.UpdatedByUserId = updatedByUserId;

            await db.SaveChangesAsync();
        }

        public async Task<List<CalcSummary>> GetResultsAsync(
            int year, string quarter, int? regionId = null,
            bool publishedOnly = false, int? specificUserId = null)
        {
            using var db = _factory.CreateDbContext();
            var query = db.Calculations
                .Include(c => c.User).ThenInclude(u => u!.Region)
                .Where(c => c.Year == year && c.Quarter == quarter);

            if (publishedOnly) query = query.Where(c => c.IsPublished && !c.IsPaused);
            if (regionId.HasValue)
                query = query.Where(c => c.User!.RegionId == regionId);
            if (specificUserId.HasValue)
                query = query.Where(c => c.UserId == specificUserId);

            var calcs = await query.OrderByDescending(c => c.FinalScore).ToListAsync();

            var calcIds = calcs.Select(c => c.CalcId).ToList();
            var allDetails = await db.CalculationDetails
                .Include(d => d.Product)
                .Where(d => calcIds.Contains(d.CalcId))
                .OrderBy(d => d.Product!.SortOrder)
                .ToListAsync();

            var detailsByCalc = allDetails.GroupBy(d => d.CalcId)
                .ToDictionary(g => g.Key, g => g.ToList());

            return calcs.Select(c => new CalcSummary
            {
                Calc = c,
                Details = detailsByCalc.TryGetValue(c.CalcId, out var d) ? d : new()
            }).ToList();
        }

        // ── Satış Etkinliği Analizi ─────────────────────────────────────

        public async Task<List<ActivityTrendPoint>> GetActivityTrendAsync(int year, int? productId = null)
        {
            using var db = _factory.CreateDbContext();
            var result = new List<ActivityTrendPoint>();
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            try
            {
                var productFilter = productId.HasValue ? " AND o.ProductId = @prodId" : "";
                // Ortak brick: ub JOIN yalnız o.UserId NULL iken (çift-sayım guard'ı) + pay çarpanı
                // (bölünen satırlar toplamı = tam değer → aylık toplam ne çiftlenir ne eksilir).
                var sql = $@"SELECT p.Month,
                    SUM(o.BoxQuantity * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS Boxes,
                    SUM(ISNULL(o.AmountTL,0) * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS AmtTL,
                    COUNT(DISTINCT COALESCE(o.UserId, ub.UserId)) AS ActiveSTs,
                    COUNT(DISTINCT o.BrickId) AS ActiveBricks
                    FROM Orders o
                    INNER JOIN Periods p ON o.PeriodId = p.PeriodId
                    LEFT JOIN UserBricks ub ON o.UserId IS NULL AND o.BrickId = ub.BrickId AND ub.IsActive = 1
                    WHERE p.Year = @year{productFilter}
                    GROUP BY p.Month
                    ORDER BY p.Month";
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                var yPrm = cmd.CreateParameter(); yPrm.ParameterName = "@year"; yPrm.Value = year; cmd.Parameters.Add(yPrm);
                if (productId.HasValue)
                {
                    var pPrm = cmd.CreateParameter(); pPrm.ParameterName = "@prodId"; pPrm.Value = productId.Value; cmd.Parameters.Add(pPrm);
                }
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    result.Add(new ActivityTrendPoint(
                        r.GetInt32(0),
                        r.IsDBNull(1) ? 0m : r.GetDecimal(1),
                        r.IsDBNull(2) ? 0m : r.GetDecimal(2),
                        r.IsDBNull(3) ? 0 : r.GetInt32(3),
                        r.IsDBNull(4) ? 0 : r.GetInt32(4)
                    ));
                }
            }
            finally { await db.Database.CloseConnectionAsync(); }
            return result;
        }

        public async Task<List<STActivityRow>> GetSTActivityMetricsAsync(int year, string quarter, int? productId = null)
        {
            ValidateQuarter(quarter);
            using var db = _factory.CreateDbContext();
            var months = quarter switch
            {
                "Q1" => new[] { 1, 2, 3 }, "Q2" => new[] { 4, 5, 6 },
                "Q3" => new[] { 7, 8, 9 }, _ => new[] { 10, 11, 12 }
            };
            var periodIds = await db.Periods
                .Where(p => p.Year == year && months.Contains(p.Month))
                .Select(p => p.PeriodId).ToListAsync();
            if (!periodIds.Any()) return new();

            var result = new List<STActivityRow>();
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            try
            {
                var pidParams = string.Join(",", periodIds.Select((_, i) => $"@p{i}"));
                var productFilter = productId.HasValue ? " AND o.ProductId = @prodId" : "";
                // Ortak brick: ub JOIN yalnız o.UserId NULL iken (çift-sayım guard'ı) + pay çarpanı.
                var sql = $@"SELECT COALESCE(o.UserId, ub.UserId) AS uid,
                    u.FullName,
                    ISNULL(r.Name,'') AS Region,
                    COUNT(DISTINCT o.BrickId) AS BrickCount,
                    COUNT(DISTINCT o.ProductId) AS ProductDiversity,
                    SUM(o.BoxQuantity * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS Boxes,
                    SUM(ISNULL(o.AmountTL,0) * CASE WHEN o.UserId IS NULL THEN ub.SharePercent / 100.0 ELSE 1 END) AS AmtTL,
                    SUM(ISNULL(o.InstitutionCount,0)) AS Institutions
                    FROM Orders o
                    LEFT JOIN UserBricks ub ON o.UserId IS NULL AND o.BrickId = ub.BrickId AND ub.IsActive = 1
                    INNER JOIN Users u ON COALESCE(o.UserId, ub.UserId) = u.UserId
                    LEFT JOIN Regions r ON u.RegionId = r.RegionId
                    WHERE u.IsActive = 1 AND u.IsDeleted = 0
                      AND COALESCE(o.UserId, ub.UserId) IS NOT NULL
                      AND o.PeriodId IN ({pidParams}){productFilter}
                    GROUP BY COALESCE(o.UserId, ub.UserId), u.FullName, r.Name
                    ORDER BY Boxes DESC";
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                for (int i = 0; i < periodIds.Count; i++)
                {
                    var prm = cmd.CreateParameter(); prm.ParameterName = $"@p{i}"; prm.Value = periodIds[i]; cmd.Parameters.Add(prm);
                }
                if (productId.HasValue)
                {
                    var pPrm = cmd.CreateParameter(); pPrm.ParameterName = "@prodId"; pPrm.Value = productId.Value; cmd.Parameters.Add(pPrm);
                }
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var uid = rd.IsDBNull(0) ? 0 : rd.GetInt32(0);
                    if (uid == 0) continue;
                    var boxes = rd.IsDBNull(5) ? 0m : rd.GetDecimal(5);
                    var inst = rd.IsDBNull(7) ? 0 : rd.GetInt32(7);
                    var boxPerInst = inst > 0 ? boxes / inst : 0m;
                    result.Add(new STActivityRow(
                        uid,
                        rd.GetString(1),
                        rd.GetString(2),
                        rd.IsDBNull(3) ? 0 : rd.GetInt32(3),
                        rd.IsDBNull(4) ? 0 : rd.GetInt32(4),
                        boxes,
                        rd.IsDBNull(6) ? 0m : rd.GetDecimal(6),
                        inst,
                        Math.Round(boxPerInst, 2)
                    ));
                }
            }
            finally { await db.Database.CloseConnectionAsync(); }
            return result;
        }

        public async Task<List<RegionProductCell>> GetRegionProductMatrixAsync(int year, string quarter)
        {
            ValidateQuarter(quarter);
            using var db = _factory.CreateDbContext();
            var months = quarter switch
            {
                "Q1" => new[] { 1, 2, 3 }, "Q2" => new[] { 4, 5, 6 },
                "Q3" => new[] { 7, 8, 9 }, _ => new[] { 10, 11, 12 }
            };
            var periodIds = await db.Periods
                .Where(p => p.Year == year && months.Contains(p.Month))
                .Select(p => p.PeriodId).ToListAsync();
            if (!periodIds.Any()) return new();

            var result = new List<RegionProductCell>();
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            try
            {
                var pidParams = string.Join(",", periodIds.Select((_, i) => $"@p{i}"));
                var sql = $@"SELECT ISNULL(r.Name,'(Bölgesiz)') AS Region,
                    pr.Name AS Product,
                    SUM(o.BoxQuantity) AS Boxes,
                    SUM(ISNULL(o.AmountTL,0)) AS AmtTL
                    FROM Orders o
                    INNER JOIN Products pr ON o.ProductId = pr.ProductId
                    LEFT JOIN UserBricks ub ON o.BrickId = ub.BrickId AND ub.IsActive = 1
                    LEFT JOIN Users u ON COALESCE(o.UserId, ub.UserId) = u.UserId
                    LEFT JOIN Regions r ON u.RegionId = r.RegionId
                    WHERE pr.IsPrimEligible = 1
                      AND o.PeriodId IN ({pidParams})
                    GROUP BY ISNULL(r.Name,'(Bölgesiz)'), pr.Name
                    ORDER BY Region, Boxes DESC";
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                for (int i = 0; i < periodIds.Count; i++)
                {
                    var prm = cmd.CreateParameter(); prm.ParameterName = $"@p{i}"; prm.Value = periodIds[i]; cmd.Parameters.Add(prm);
                }
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    result.Add(new RegionProductCell(
                        rd.GetString(0),
                        rd.GetString(1),
                        rd.IsDBNull(2) ? 0m : rd.GetDecimal(2),
                        rd.IsDBNull(3) ? 0m : rd.GetDecimal(3)
                    ));
                }
            }
            finally { await db.Database.CloseConnectionAsync(); }
            return result;
        }

        // ── Brick & Eczane Performansı (ST/BM panoları) ─────────────────────────

        private int[] QuarterMonths(string quarter) => quarter switch
        {
            "Q1" => new[] { 1, 2, 3 }, "Q2" => new[] { 4, 5, 6 },
            "Q3" => new[] { 7, 8, 9 }, _ => new[] { 10, 11, 12 }
        };

        /// <summary>
        /// Brick bazlı realizasyon (sell-out / hedef). userId verilirse o ST'nin
        /// brickleri (UserBricks), regionId verilirse bölgedeki brickler kapsanır.
        /// </summary>
        public async Task<List<BrickPerfRow>> GetBrickPerformanceAsync(
            int year, string quarter, int? regionId, int? userId, int? productId)
        {
            var rows = new List<BrickPerfRow>();
            quarter = (quarter ?? "").Trim().ToUpperInvariant();
            if (!_validQuarters.Contains(quarter)) return rows;
            try
            {
                using var db = _factory.CreateDbContext();
                var periodIds = await db.Periods
                    .Where(p => p.Year == year && QuarterMonths(quarter).Contains(p.Month))
                    .Select(p => p.PeriodId).ToListAsync();
                if (!periodIds.Any()) return rows;

                var conn = db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync();
                try
                {
                var pidParams = string.Join(",", periodIds.Select((_, i) => $"@p{i}"));
                var prodFilter = productId.HasValue ? " AND ProductId = @prodId" : "";
                var targetUserFilter = userId.HasValue ? " AND UserId = @uid" : "";
                var scopeJoin = userId.HasValue
                    ? "INNER JOIN UserBricks ub ON ub.BrickId = b.BrickId AND ub.IsActive = 1 AND ub.UserId = @uid"
                    : "";
                var regionWhere = regionId.HasValue ? " AND b.RegionId = @rid" : "";

                var sql = $@"
                    SELECT b.BrickId, b.Name, ISNULL(b.City,'') AS City,
                        ISNULL(t.Target,0) AS Target, ISNULL(s.SellOut,0) AS SellOut
                    FROM Bricks b
                    {scopeJoin}
                    LEFT JOIN (
                        SELECT BrickId, SUM(TargetQty) AS Target FROM Targets
                        WHERE PeriodId IN ({pidParams}){prodFilter}{targetUserFilter}
                        GROUP BY BrickId
                    ) t ON t.BrickId = b.BrickId
                    LEFT JOIN (
                        SELECT BrickId, SUM(Quantity) AS SellOut FROM SellOuts
                        WHERE PeriodId IN ({pidParams}) AND Quantity > 0{prodFilter}
                        GROUP BY BrickId
                    ) s ON s.BrickId = b.BrickId
                    WHERE b.IsActive = 1{regionWhere}
                      AND (ISNULL(t.Target,0) > 0 OR ISNULL(s.SellOut,0) > 0)
                    ORDER BY b.Name
                    OPTION (RECOMPILE)";

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 60;
                for (int i = 0; i < periodIds.Count; i++)
                {
                    var prm = cmd.CreateParameter(); prm.ParameterName = $"@p{i}"; prm.Value = periodIds[i]; cmd.Parameters.Add(prm);
                }
                if (productId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@prodId"; p.Value = productId.Value; cmd.Parameters.Add(p); }
                if (userId.HasValue)   { var p = cmd.CreateParameter(); p.ParameterName = "@uid";    p.Value = userId.Value;    cmd.Parameters.Add(p); }
                if (regionId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@rid";    p.Value = regionId.Value;  cmd.Parameters.Add(p); }

                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var target  = r.IsDBNull(3) ? 0m : r.GetDecimal(3);
                    var sellOut = r.IsDBNull(4) ? 0m : r.GetDecimal(4);
                    var realization = target > 0 ? sellOut / target : 0m;
                    rows.Add(new BrickPerfRow(r.GetInt32(0), r.GetString(1), r.GetString(2), target, sellOut, realization));
                }
                }
                finally { await db.Database.CloseConnectionAsync(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetBrickPerformanceAsync başarısız (year={Year} quarter={Quarter} region={Region} user={User})", year, quarter, regionId, userId);
            }
            return rows;
        }

        /// <summary>
        /// Bölgenin toplam sell-out'u (net): Units = SUM(Quantity), AmountTL = SUM(AmountTL).
        /// Brick.RegionId üzerinden filtrelenir (GetBrickPerformanceAsync ile aynı atıf yolu).
        /// İade (negatif) satırları nete dahildir (P15-C). Hata/eksik veride (0,0) döner — pano çökmez.
        /// assignedOnly=true iken yalnız aktif bir ST'ye atanmış brick'ler (UserBricks) sayılır —
        /// "ST'lerin sorumlu olduğu brick'ten çıkan sell-out" filtresi; atanmamış brick hacmi hariç.
        /// </summary>
        public async Task<(decimal Units, decimal AmountTL)> GetRegionSalesTotalsAsync(
            int year, string quarter, int? regionId, int? productId = null, bool assignedOnly = false)
        {
            quarter = (quarter ?? "").Trim().ToUpperInvariant();
            if (!_validQuarters.Contains(quarter)) return (0m, 0m);
            try
            {
                using var db = _factory.CreateDbContext();
                var periodIds = await db.Periods
                    .Where(p => p.Year == year && QuarterMonths(quarter).Contains(p.Month))
                    .Select(p => p.PeriodId).ToListAsync();
                if (!periodIds.Any()) return (0m, 0m);

                var q = from s in db.SellOuts
                        join b in db.Bricks on s.BrickId equals b.BrickId
                        where periodIds.Contains(s.PeriodId)
                              && (!regionId.HasValue || b.RegionId == regionId)
                              && (!productId.HasValue || s.ProductId == productId)
                              && (!assignedOnly || db.UserBricks.Any(ub => ub.BrickId == s.BrickId && ub.IsActive))
                        select s;

                var agg = await q
                    .GroupBy(_ => 1)
                    .Select(g => new
                    {
                        Units = g.Sum(x => x.Quantity),
                        Tl = g.Sum(x => (decimal?)x.AmountTL) ?? 0m
                    })
                    .FirstOrDefaultAsync();

                return agg == null ? (0m, 0m) : (agg.Units, agg.Tl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetRegionSalesTotalsAsync başarısız (year={Year} quarter={Quarter} region={Region})", year, quarter, regionId);
                return (0m, 0m);
            }
        }

        /// <summary>
        /// Bricklerdeki en yüksek hacimli eczaneler (GLN/kurum) — SellOutDetails'ten.
        /// userId → ST'nin brickleri; regionId → bölge brickleri. productId opsiyonel.
        /// </summary>
        public async Task<List<PharmacyPerfRow>> GetTopPharmaciesAsync(
            int year, string quarter, int? regionId, int? userId, int? productId, int top = 15)
        {
            var rows = new List<PharmacyPerfRow>();
            quarter = (quarter ?? "").Trim().ToUpperInvariant();
            if (!_validQuarters.Contains(quarter)) return rows;
            try
            {
                using var db = _factory.CreateDbContext();
                var periodIds = await db.Periods
                    .Where(p => p.Year == year && QuarterMonths(quarter).Contains(p.Month))
                    .Select(p => p.PeriodId).ToListAsync();
                if (!periodIds.Any()) return rows;

                var conn = db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync();
                try
                {
                    var pidParams = string.Join(",", periodIds.Select((_, i) => $"@p{i}"));
                    var prodFilter = productId.HasValue ? " AND sod.ProductId = @prodId" : "";
                    var scopeJoin = userId.HasValue
                        ? "INNER JOIN UserBricks ub ON ub.BrickId = sod.BrickId AND ub.IsActive = 1 AND ub.UserId = @uid"
                        : "";
                    var regionWhere = regionId.HasValue ? " AND b.RegionId = @rid" : "";

                    var sql = $@"
                        SELECT TOP (@top) sod.GLN,
                            MAX(ISNULL(sod.KurumName,'')) AS Kurum,
                            MAX(ISNULL(sod.Il,'')) AS Il,
                            MAX(ISNULL(sod.Ilce,'')) AS Ilce,
                            MAX(b.Name) AS BrickName,
                            SUM(sod.Quantity) AS Boxes,
                            SUM(ISNULL(sod.AmountTL,0)) AS AmtTL
                        FROM SellOutDetails sod
                        INNER JOIN Bricks b ON b.BrickId = sod.BrickId
                        {scopeJoin}
                        WHERE sod.PeriodId IN ({pidParams}){prodFilter}{regionWhere}
                        GROUP BY sod.GLN
                        ORDER BY SUM(sod.Quantity) DESC
                        OPTION (RECOMPILE)";

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 60;
                    var topPrm = cmd.CreateParameter(); topPrm.ParameterName = "@top"; topPrm.Value = top; cmd.Parameters.Add(topPrm);
                    for (int i = 0; i < periodIds.Count; i++)
                    {
                        var prm = cmd.CreateParameter(); prm.ParameterName = $"@p{i}"; prm.Value = periodIds[i]; cmd.Parameters.Add(prm);
                    }
                    if (productId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@prodId"; p.Value = productId.Value; cmd.Parameters.Add(p); }
                    if (userId.HasValue)   { var p = cmd.CreateParameter(); p.ParameterName = "@uid";    p.Value = userId.Value;    cmd.Parameters.Add(p); }
                    if (regionId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@rid";    p.Value = regionId.Value;  cmd.Parameters.Add(p); }

                    using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                    {
                        rows.Add(new PharmacyPerfRow(
                            r.IsDBNull(0) ? "" : r.GetString(0),
                            r.IsDBNull(1) ? "" : r.GetString(1),
                            r.IsDBNull(2) ? "" : r.GetString(2),
                            r.IsDBNull(3) ? "" : r.GetString(3),
                            r.IsDBNull(4) ? "" : r.GetString(4),
                            r.IsDBNull(5) ? 0m : r.GetDecimal(5),
                            r.IsDBNull(6) ? 0m : r.GetDecimal(6)));
                    }
                }
                finally { await db.Database.CloseConnectionAsync(); }
            }
            catch (Exception ex)
            {
                // SellOutDetails tablosu yoksa/erişilemezse panoyu çökertme; boş liste dön.
                _logger.LogError(ex, "GetTopPharmaciesAsync başarısız (year={Year} quarter={Quarter} region={Region} user={User}) — SellOutDetails yok/erişilemez olabilir", year, quarter, regionId, userId);
            }
            return rows;
        }

        /// <summary>
        /// Planlı/plansız eczane ayrımı için <c>agg</c> CTE'sinin WHERE'ine eklenen yüklem.
        /// "Planlı" = o yıl için PharmacyPlans'ta kaydı olan GLN (UTT_Brick ziyaret planı importu).
        /// EXISTS/NOT EXISTS kullanılır (JOIN değil): üç sorguda da agg zaten sod.GLN'e göre grupluyor,
        /// böylece dış sorgunun WHERE/JOIN kompozisyonuna hiç dokunmadan ve satır çoğaltmadan filtreler.
        /// IX_PharmacyPlans_Year_GLN üzerinden gider. null → yüklem hiç eklenmez (bugünkü davranış birebir
        /// korunur; db/pharmacy-plans.sql çalıştırılmamış olsa bile varsayılan görünüm etkilenmez).
        /// </summary>
        private static string PlanPredicate(bool? plannedOnly) => plannedOnly switch
        {
            true  => " AND EXISTS (SELECT 1 FROM PharmacyPlans pp WHERE pp.GLN = sod.GLN AND pp.[Year] = @planYear)",
            false => " AND NOT EXISTS (SELECT 1 FROM PharmacyPlans pp WHERE pp.GLN = sod.GLN AND pp.[Year] = @planYear)",
            _     => ""
        };

        /// <summary>
        /// Kapsamdaki TÜM eczaneler (GLN bazında), brick bilgisiyle — Eczane Analizi sayfası için.
        /// Brick = Institution master (varsa, admin override dahil) yoksa SellOutDetail brick'i (COALESCE).
        /// userId → ST'nin brickleri (UserBricks); regionId → bölge; ikisi de null → tümü. productId opsiyonel.
        /// Institutions tablosunun YALNIZ orijinal kolonları (GLN/BrickId/InstitutionId) kullanılır —
        /// db/institutions.sql çalışmasa bile (tablo boş) COALESCE detay brick'e düşer, liste yine gelir.
        /// plannedOnly: null=tümü, true=yalnız ziyaret planındaki eczaneler, false=yalnız plansızlar.
        /// </summary>
        public async Task<List<PharmacyRow>> GetPharmaciesForScopeAsync(
            int year, string quarter, int? regionId, int? userId, int? productId, bool? plannedOnly = null)
        {
            var rows = new List<PharmacyRow>();
            quarter = (quarter ?? "").Trim().ToUpperInvariant();
            if (!_validQuarters.Contains(quarter)) return rows;
            try
            {
                using var db = _factory.CreateDbContext();
                var periodIds = await db.Periods
                    .Where(p => p.Year == year && QuarterMonths(quarter).Contains(p.Month))
                    .Select(p => p.PeriodId).ToListAsync();
                if (!periodIds.Any()) return rows;

                var conn = db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync();
                try
                {
                    var pidParams = string.Join(",", periodIds.Select((_, i) => $"@p{i}"));
                    var prodFilter = productId.HasValue ? " AND sod.ProductId = @prodId" : "";
                    var planFilter = PlanPredicate(plannedOnly);
                    var scopeJoin = userId.HasValue
                        ? "INNER JOIN UserBricks ub ON ub.BrickId = b.BrickId AND ub.IsActive = 1 AND ub.UserId = @uid"
                        : "";
                    var regionWhere = regionId.HasValue ? " WHERE b.RegionId = @rid" : "";

                    var sql = $@"
                        WITH agg AS (
                            SELECT sod.GLN,
                                MAX(ISNULL(sod.KurumName,'')) AS Kurum,
                                MAX(ISNULL(sod.Il,'')) AS Il,
                                MAX(ISNULL(sod.Ilce,'')) AS Ilce,
                                MAX(sod.BrickId) AS DetailBrickId,
                                SUM(sod.Quantity) AS Boxes,
                                SUM(ISNULL(sod.AmountTL,0)) AS AmtTL
                            FROM SellOutDetails sod
                            WHERE sod.PeriodId IN ({pidParams}){prodFilter}{planFilter}
                            GROUP BY sod.GLN
                        )
                        SELECT ISNULL(i.InstitutionId,0) AS InstId,
                            b.BrickId, b.Name AS BrickName,
                            agg.GLN, agg.Kurum, agg.Il, agg.Ilce, agg.Boxes, agg.AmtTL
                        FROM agg
                        LEFT JOIN Institutions i ON i.GLN = agg.GLN
                        INNER JOIN Bricks b ON b.BrickId = COALESCE(i.BrickId, agg.DetailBrickId)
                        {scopeJoin}{regionWhere}
                        ORDER BY b.Name, agg.Boxes DESC
                        OPTION (RECOMPILE)";

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 60;
                    for (int i = 0; i < periodIds.Count; i++)
                    {
                        var prm = cmd.CreateParameter(); prm.ParameterName = $"@p{i}"; prm.Value = periodIds[i]; cmd.Parameters.Add(prm);
                    }
                    if (productId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@prodId"; p.Value = productId.Value; cmd.Parameters.Add(p); }
                    if (plannedOnly.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@planYear"; p.Value = year; cmd.Parameters.Add(p); }
                    if (userId.HasValue)   { var p = cmd.CreateParameter(); p.ParameterName = "@uid";    p.Value = userId.Value;    cmd.Parameters.Add(p); }
                    if (regionId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@rid";    p.Value = regionId.Value;  cmd.Parameters.Add(p); }

                    using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                    {
                        rows.Add(new PharmacyRow(
                            r.IsDBNull(0) ? 0 : r.GetInt32(0),
                            r.IsDBNull(1) ? (int?)null : r.GetInt32(1),
                            r.IsDBNull(2) ? "" : r.GetString(2),
                            r.IsDBNull(3) ? "" : r.GetString(3),
                            r.IsDBNull(4) ? "" : r.GetString(4),
                            r.IsDBNull(5) ? "" : r.GetString(5),
                            r.IsDBNull(6) ? "" : r.GetString(6),
                            r.IsDBNull(7) ? 0m : r.GetDecimal(7),
                            r.IsDBNull(8) ? 0m : r.GetDecimal(8)));
                    }
                }
                finally { await db.Database.CloseConnectionAsync(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetPharmaciesForScopeAsync başarısız (year={Year} quarter={Quarter} region={Region} user={User})", year, quarter, regionId, userId);
            }
            return rows;
        }

        /// <summary>
        /// Eczane Analizi VARSAYILAN görünümü: brick bazında ÖZET (eczane sayısı + units + TL).
        /// 27k eczane satırı yerine ~500 brick satırı → hızlı SQL (kapsayıcı indeks) + küçük payload.
        /// Etkin brick = COALESCE(Institution.BrickId, detay brick). Kapsam GetPharmaciesForScope ile aynı.
        /// </summary>
        public async Task<List<BrickSummaryRow>> GetPharmacyBrickSummaryAsync(
            int year, string quarter, int? regionId, int? userId, int? productId, bool? plannedOnly = null)
        {
            var rows = new List<BrickSummaryRow>();
            quarter = (quarter ?? "").Trim().ToUpperInvariant();
            if (!_validQuarters.Contains(quarter)) return rows;
            try
            {
                using var db = _factory.CreateDbContext();
                var periodIds = await db.Periods
                    .Where(p => p.Year == year && QuarterMonths(quarter).Contains(p.Month))
                    .Select(p => p.PeriodId).ToListAsync();
                if (!periodIds.Any()) return rows;

                var conn = db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync();
                try
                {
                    var pidParams = string.Join(",", periodIds.Select((_, i) => $"@p{i}"));
                    var prodFilter = productId.HasValue ? " AND sod.ProductId = @prodId" : "";
                    var planFilter = PlanPredicate(plannedOnly);
                    var scopeJoin = userId.HasValue
                        ? "INNER JOIN UserBricks ub ON ub.BrickId = b.BrickId AND ub.IsActive = 1 AND ub.UserId = @uid"
                        : "";
                    var regionWhere = regionId.HasValue ? " WHERE b.RegionId = @rid" : "";

                    var sql = $@"
                        WITH agg AS (
                            SELECT sod.GLN, MAX(sod.BrickId) AS DetailBrickId,
                                SUM(sod.Quantity) AS Boxes, SUM(ISNULL(sod.AmountTL,0)) AS AmtTL
                            FROM SellOutDetails sod
                            WHERE sod.PeriodId IN ({pidParams}){prodFilter}{planFilter}
                            GROUP BY sod.GLN
                        ), eff AS (
                            SELECT COALESCE(i.BrickId, agg.DetailBrickId) AS BrickId, agg.Boxes, agg.AmtTL
                            FROM agg LEFT JOIN Institutions i ON i.GLN = agg.GLN
                        )
                        SELECT b.BrickId, b.Name AS BrickName, ISNULL(b.City,'') AS City,
                            COUNT(*) AS Eczane, SUM(eff.Boxes) AS Boxes, SUM(eff.AmtTL) AS AmtTL
                        FROM eff
                        INNER JOIN Bricks b ON b.BrickId = eff.BrickId
                        {scopeJoin}{regionWhere}
                        GROUP BY b.BrickId, b.Name, b.City
                        ORDER BY SUM(eff.Boxes) DESC
                        OPTION (RECOMPILE)";

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql; cmd.CommandTimeout = 60;
                    for (int i = 0; i < periodIds.Count; i++)
                    { var prm = cmd.CreateParameter(); prm.ParameterName = $"@p{i}"; prm.Value = periodIds[i]; cmd.Parameters.Add(prm); }
                    if (productId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@prodId"; p.Value = productId.Value; cmd.Parameters.Add(p); }
                    if (plannedOnly.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@planYear"; p.Value = year; cmd.Parameters.Add(p); }
                    if (userId.HasValue)   { var p = cmd.CreateParameter(); p.ParameterName = "@uid";    p.Value = userId.Value;    cmd.Parameters.Add(p); }
                    if (regionId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@rid";    p.Value = regionId.Value;  cmd.Parameters.Add(p); }

                    using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                        rows.Add(new BrickSummaryRow(
                            r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                            r.IsDBNull(3) ? 0 : r.GetInt32(3),
                            r.IsDBNull(4) ? 0m : r.GetDecimal(4), r.IsDBNull(5) ? 0m : r.GetDecimal(5)));
                }
                finally { await db.Database.CloseConnectionAsync(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetPharmacyBrickSummaryAsync başarısız (year={Year} quarter={Quarter})", year, quarter);
            }
            return rows;
        }

        /// <summary>Tek bir brick'in eczaneleri (drill-down) — GLN bazında units/TL.</summary>
        public async Task<List<PharmacyRow>> GetPharmaciesForBrickAsync(int brickId, int year, string quarter, int? productId, bool? plannedOnly = null)
        {
            var rows = new List<PharmacyRow>();
            quarter = (quarter ?? "").Trim().ToUpperInvariant();
            if (!_validQuarters.Contains(quarter)) return rows;
            try
            {
                using var db = _factory.CreateDbContext();
                var periodIds = await db.Periods
                    .Where(p => p.Year == year && QuarterMonths(quarter).Contains(p.Month))
                    .Select(p => p.PeriodId).ToListAsync();
                if (!periodIds.Any()) return rows;

                var conn = db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync();
                try
                {
                    var pidParams = string.Join(",", periodIds.Select((_, i) => $"@p{i}"));
                    var prodFilter = productId.HasValue ? " AND sod.ProductId = @prodId" : "";
                    var planFilter = PlanPredicate(plannedOnly);
                    var sql = $@"
                        WITH agg AS (
                            SELECT sod.GLN, MAX(ISNULL(sod.KurumName,'')) AS Kurum,
                                MAX(ISNULL(sod.Il,'')) AS Il, MAX(ISNULL(sod.Ilce,'')) AS Ilce,
                                MAX(sod.BrickId) AS DetailBrickId,
                                SUM(sod.Quantity) AS Boxes, SUM(ISNULL(sod.AmountTL,0)) AS AmtTL
                            FROM SellOutDetails sod
                            WHERE sod.PeriodId IN ({pidParams}){prodFilter}{planFilter}
                            GROUP BY sod.GLN
                        )
                        SELECT ISNULL(i.InstitutionId,0) AS InstId, b.BrickId, b.Name AS BrickName,
                            agg.GLN, agg.Kurum, agg.Il, agg.Ilce, agg.Boxes, agg.AmtTL
                        FROM agg
                        LEFT JOIN Institutions i ON i.GLN = agg.GLN
                        INNER JOIN Bricks b ON b.BrickId = COALESCE(i.BrickId, agg.DetailBrickId)
                        WHERE b.BrickId = @brickId
                        ORDER BY agg.Boxes DESC
                        OPTION (RECOMPILE)";

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql; cmd.CommandTimeout = 60;
                    for (int i = 0; i < periodIds.Count; i++)
                    { var prm = cmd.CreateParameter(); prm.ParameterName = $"@p{i}"; prm.Value = periodIds[i]; cmd.Parameters.Add(prm); }
                    if (productId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@prodId"; p.Value = productId.Value; cmd.Parameters.Add(p); }
                    if (plannedOnly.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@planYear"; p.Value = year; cmd.Parameters.Add(p); }
                    var bp = cmd.CreateParameter(); bp.ParameterName = "@brickId"; bp.Value = brickId; cmd.Parameters.Add(bp);

                    using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                        rows.Add(new PharmacyRow(
                            r.IsDBNull(0) ? 0 : r.GetInt32(0), r.IsDBNull(1) ? (int?)null : r.GetInt32(1),
                            r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
                            r.IsDBNull(4) ? "" : r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5),
                            r.IsDBNull(6) ? "" : r.GetString(6), r.IsDBNull(7) ? 0m : r.GetDecimal(7),
                            r.IsDBNull(8) ? 0m : r.GetDecimal(8)));
                }
                finally { await db.Database.CloseConnectionAsync(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetPharmaciesForBrickAsync başarısız (brick={Brick})", brickId);
            }
            return rows;
        }

        /// <summary>
        /// Bölge Analizi: her bölge için Hedef + Gerçekleşen (sell-out) + brick/ST sayısı.
        /// Hedef = Targets→Brick.RegionId; Gerçekleşen = SellOuts→Brick.RegionId (bir brick tek bölgede).
        /// regionId verilirse yalnız o bölge (BM). productId opsiyonel.
        /// </summary>
        public async Task<List<RegionSummaryRow>> GetRegionSummaryAsync(
            int year, string quarter, int? regionId, int? productId)
        {
            var rows = new List<RegionSummaryRow>();
            quarter = (quarter ?? "").Trim().ToUpperInvariant();
            if (!_validQuarters.Contains(quarter)) return rows;
            try
            {
                using var db = _factory.CreateDbContext();
                var periodIds = await db.Periods
                    .Where(p => p.Year == year && QuarterMonths(quarter).Contains(p.Month))
                    .Select(p => p.PeriodId).ToListAsync();
                if (!periodIds.Any()) return rows;

                var conn = db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync();
                try
                {
                    var pidParams = string.Join(",", periodIds.Select((_, i) => $"@p{i}"));
                    var prodT = productId.HasValue ? " AND t.ProductId = @prodId" : "";
                    var prodS = productId.HasValue ? " AND s.ProductId = @prodId" : "";
                    var regionWhere = regionId.HasValue ? " AND rg.RegionId = @rid" : "";

                    var sql = $@"
                        SELECT rg.RegionId, rg.Name,
                            ISNULL(th.Hedef,0) AS Hedef, ISNULL(so.Gerc,0) AS Gerc, ISNULL(so.GercTL,0) AS GercTL,
                            (SELECT COUNT(*) FROM Bricks bk WHERE bk.RegionId=rg.RegionId AND bk.IsActive=1) AS BrickCount,
                            (SELECT COUNT(*) FROM Users u WHERE u.RegionId=rg.RegionId AND u.IsActive=1 AND u.IsDeleted=0 AND u.RoleId=@stRole) AS STCount
                        FROM Regions rg
                        LEFT JOIN (SELECT b.RegionId, SUM(t.TargetQty) AS Hedef
                                   FROM Targets t JOIN Bricks b ON b.BrickId=t.BrickId
                                   WHERE t.PeriodId IN ({pidParams}){prodT} GROUP BY b.RegionId) th ON th.RegionId=rg.RegionId
                        LEFT JOIN (SELECT b.RegionId, SUM(s.Quantity) AS Gerc, SUM(ISNULL(s.AmountTL,0)) AS GercTL
                                   FROM SellOuts s JOIN Bricks b ON b.BrickId=s.BrickId
                                   WHERE s.PeriodId IN ({pidParams}){prodS} GROUP BY b.RegionId) so ON so.RegionId=rg.RegionId
                        WHERE rg.IsActive=1{regionWhere}
                        ORDER BY ISNULL(so.Gerc,0) DESC";

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql; cmd.CommandTimeout = 60;
                    for (int i = 0; i < periodIds.Count; i++)
                    { var prm = cmd.CreateParameter(); prm.ParameterName = $"@p{i}"; prm.Value = periodIds[i]; cmd.Parameters.Add(prm); }
                    var rp = cmd.CreateParameter(); rp.ParameterName = "@stRole"; rp.Value = (int)RoleId.ST; cmd.Parameters.Add(rp);
                    if (productId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@prodId"; p.Value = productId.Value; cmd.Parameters.Add(p); }
                    if (regionId.HasValue) { var p = cmd.CreateParameter(); p.ParameterName = "@rid"; p.Value = regionId.Value; cmd.Parameters.Add(p); }

                    using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                        rows.Add(new RegionSummaryRow(
                            r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1),
                            r.IsDBNull(2) ? 0m : r.GetDecimal(2), r.IsDBNull(3) ? 0m : r.GetDecimal(3),
                            r.IsDBNull(4) ? 0m : r.GetDecimal(4),
                            r.IsDBNull(5) ? 0 : r.GetInt32(5), r.IsDBNull(6) ? 0 : r.GetInt32(6)));
                }
                finally { await db.Database.CloseConnectionAsync(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetRegionSummaryAsync başarısız (year={Year} quarter={Quarter})", year, quarter);
            }
            return rows;
        }
    }

    public class CalcResult
    {
        public int Year { get; set; }
        public string Quarter { get; set; } = "";
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int STCount { get; set; }
        public int PassedCount { get; set; }
        public List<STCalcResult> STResults { get; set; } = new();

        // Run-rate teşhisi — hesaplamada hedef orantılandı mı, hangi oranla
        public bool RunRateMode { get; set; }
        public int SoldPeriods { get; set; }
        public int TotalPeriods { get; set; }
    }

    public class STCalcResult
    {
        public int UserId { get; set; }
        public string FullName { get; set; } = "";
        public decimal TotalScore { get; set; }
        public decimal FinalScore { get; set; }
        public bool PassedGate { get; set; }
        public bool IsEligible { get; set; }
        public string? IneligibleReason { get; set; }
        public decimal? RutaScore { get; set; }
        public decimal DistScore { get; set; }
        public decimal OrderScore { get; set; }
        public decimal PrimAmount { get; set; }        // Hesaplanan prim tutarı (TL)
        public int ProductsWithTarget { get; set; }   // Hedefi olan ürün sayısı
        public int ProductsWithSellOut { get; set; }  // Satış olan ürün sayısı
    }

    public class CalcSummary
    {
        public Calculation Calc { get; set; } = null!;
        public List<CalculationDetail> Details { get; set; } = new();
    }

    public class DataCheckResult
    {
        public int PeriodCount { get; set; }
        public int TargetCount { get; set; }
        public int SellOutCount { get; set; }
        public int UserBricksCount { get; set; }
        public int WeightsCount { get; set; }
        public int STCount { get; set; }
    }

    // Bir çeyreğin hesaplama tazeliği: HasCalc=hiç hesaplanmış mı, LastCalc=son hesaplama zamanı,
    // AssignmentsStale=brick ataması son hesaplamadan sonra değişti mi, Published=yayınlanmış mı.
    public record CalcFreshness(bool HasCalc, DateTime? LastCalc, bool AssignmentsStale, bool Published);

    public record OrderLeaderRow(
        int Rank,
        int? UserId,
        string FullName,
        string Region,
        decimal TotalBoxes,
        decimal TotalTL,
        int UniquePharmacies);

    public class RegionAnalyticsRow
    {
        public string RegionName { get; set; } = "";
        public int StCount { get; set; }
        public int PassedCount { get; set; }
        public double PassRate { get; set; }
        public double AvgFinalScore { get; set; }
        public double AvgSalesScore { get; set; }
        public double AvgRutaScore { get; set; }
        public double AvgDistScore { get; set; }
        public double AvgStrikeScore { get; set; }
    }

    public record ActivityTrendPoint(
        int Month,
        decimal TotalBoxes,
        decimal TotalTL,
        int ActiveSTs,
        int ActiveBricks);

    public record STActivityRow(
        int UserId,
        string FullName,
        string Region,
        int BrickCount,
        int ProductDiversity,
        decimal TotalBoxes,
        decimal TotalTL,
        int TotalInstitutions,
        decimal BoxPerInstitution);

    public record RegionProductCell(
        string RegionName,
        string ProductName,
        decimal TotalBoxes,
        decimal TotalTL);

    public record BrickPerfRow(
        int BrickId,
        string BrickName,
        string City,
        decimal Target,
        decimal SellOut,
        decimal Realization);

    public record PharmacyPerfRow(
        string GLN,
        string KurumName,
        string Il,
        string Ilce,
        string BrickName,
        decimal Boxes,
        decimal AmountTL);

    /// <summary>Bölge Analizi satırı — bölge bazında hedef/gerçekleşen (adet+TL) + brick/ST sayısı.</summary>
    public record RegionSummaryRow(
        int RegionId,
        string Name,
        decimal Hedef,
        decimal Gerceklesen,
        decimal GerceklesenTL,
        int BrickCount,
        int STCount);

    /// <summary>Eczane Analizi varsayılan görünümü — brick bazında özet.</summary>
    public record BrickSummaryRow(
        int BrickId,
        string BrickName,
        string City,
        int PharmacyCount,
        decimal Boxes,
        decimal AmountTL);

    /// <summary>Eczane Analizi satırı — brick bazında gruplama için BrickId/Name taşır.</summary>
    public record PharmacyRow(
        int InstitutionId,
        int? BrickId,
        string BrickName,
        string GLN,
        string KurumName,
        string Il,
        string Ilce,
        decimal Boxes,
        decimal AmountTL);
}