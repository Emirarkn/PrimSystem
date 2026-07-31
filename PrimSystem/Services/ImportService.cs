using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using OfficeOpenXml;
using PrimSystem.Data;
using System.Data;
using System.Security.Cryptography;

namespace PrimSystem.Services
{
    public class ImportService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly AuditService _audit;

        public ImportService(IDbContextFactory<AppDbContext> factory, AuditService audit)
        {
            _factory = factory;
            _audit = audit;
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        }

        // Eşleşmeyen değerleri (brick/ürün/dönem) sebep bazında özetler — büyük dosyada
        // her satır için ayrı hata yerine distinct değer + sayı raporlar.
        private static void AppendUnmatched(List<string> errors, string label, HashSet<string> values, int cap = 25)
        {
            if (values.Count == 0) return;
            var shown = string.Join(", ", values.Take(cap));
            var more = values.Count > cap ? $" (+{values.Count - cap} farklı daha)" : "";
            errors.Add($"{label} — {values.Count} farklı değer eşleşmedi: {shown}{more}");
        }

        // InfiniLens import takma adı tek alanda birden çok marka tutabilir (';' '|' veya
        // satır sonu ile ayrılır). Örn. "DİĞER İTRİYAT" ürünü = JOHNSON'S BABY;NEUTROGENA;CAREFREE.
        // Tek değerde tek öğe döner — geriye uyumlu.
        internal static IEnumerable<string> SplitAliases(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) yield break;
            foreach (var part in raw.Split(new[] { ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var a = Normalize(part);
                if (a.Length > 0) yield return a;
            }
        }

        // Ürün eşleme haritası: Name öncelikli (kanonik/hedef adı), ardından her alias
        // TryAdd ile eklenir (Name'i ezmez). Üç import da (Sell-Out/Sipariş/Hedef) bunu kullanır.
        internal static Dictionary<string, int> BuildProductMap(IEnumerable<(int ProductId, string Name, string? ImportAlias)> products)
        {
            var map = new Dictionary<string, int>();
            foreach (var p in products)
                map[Normalize(p.Name)] = p.ProductId;
            foreach (var p in products)
                foreach (var alias in SplitAliases(p.ImportAlias))
                    map.TryAdd(alias, p.ProductId);
            return map;
        }

        // ── Eşleştirme: kanonik anahtar + bulanık öneri ──────────────────────
        /// <summary>
        /// Eşleştirme için TEK kanonik anahtar üretir: trim → iç boşlukları tek boşluğa indir →
        /// Türkçe nokta sorununu çöz (i/İ/ı/I hepsi 'I') → InvariantCulture büyük harf.
        /// Kaynaklar arası "İzmir/Izmir", "İTRİYAT/ITRIYAT", çift boşluk gibi sessiz ıskalamaları
        /// giderir. ş/ç/ğ/ü/ö KATLANMAZ (yanlış birleşme riski); yalnız nokta sorunu çözülür.
        /// Harita kurarken VE dosya değerini ararken aynı işlev kullanılmalı.
        /// </summary>
        internal static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s.Trim())
            {
                char c = ch is 'ı' or 'İ' or 'i' or 'I' ? 'I' : ch;
                if (char.IsWhiteSpace(c))
                {
                    if (prevSpace) continue;
                    sb.Append(' '); prevSpace = true;
                }
                else { sb.Append(c); prevSpace = false; }
            }
            return sb.ToString().ToUpperInvariant();
        }

        /// <summary>İki dizgenin Levenshtein düzenleme uzaklığı (O(n·m), satır-çiftli).</summary>
        internal static int LevenshteinDistance(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 0; i < a.Length; i++)
            {
                cur[0] = i + 1;
                for (int j = 0; j < b.Length; j++)
                {
                    int cost = a[i] == b[j] ? 0 : 1;
                    cur[j + 1] = Math.Min(Math.Min(cur[j] + 1, prev[j + 1] + 1), prev[j] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }

        /// <summary>
        /// Eşleşmeyen bir değer için aday kümesinden en yakın eşi önerir (normalize edilmiş
        /// Levenshtein benzerliği ≥ eşik). Eşik altındaysa null. YALNIZ öneri — otomatik eşleme yok.
        /// </summary>
        internal static string? SuggestMatch(string value, IEnumerable<string> candidates, double minSimilarity = 0.82)
        {
            var nv = Normalize(value);
            if (nv.Length == 0) return null;
            string? best = null; double bestSim = 0;
            foreach (var cand in candidates)
            {
                var nc = Normalize(cand);
                if (nc.Length == 0) continue;
                double sim = 1.0 - (double)LevenshteinDistance(nv, nc) / Math.Max(nv.Length, nc.Length);
                if (sim > bestSim) { bestSim = sim; best = cand; }
            }
            return bestSim >= minSimilarity ? best : null;
        }

        /// <summary>
        /// Eşleşmeyen değerler için yapısal öneri üretir (dry-run önizleme). Aday haritası
        /// (normalize anahtar → varlık Id) verilir; öneri kabul edilince Id ile kalıcı eşleme yapılır.
        /// </summary>
        internal static void AppendSuggestions(List<MatchSuggestion> sink, string type,
            IEnumerable<string> unmatched, IReadOnlyDictionary<string, int> candidateMap, int cap = 15)
        {
            var keys = candidateMap.Keys.ToList();
            int n = 0;
            foreach (var v in unmatched)
            {
                if (n >= cap) break;
                var sug = SuggestMatch(v, keys);
                if (sug != null && candidateMap.TryGetValue(sug, out var targetId))
                {
                    sink.Add(new MatchSuggestion { Type = type, Source = v, Suggestion = sug, TargetId = targetId });
                    n++;
                }
            }
        }

        // Veri içe aktarma yalnızca Admin'e açıktır (UI'da da yalnızca Admin görür).
        // Çağıranın rolünü servis katmanında doğrula — UI gizlemesine güvenme.
        private async Task EnsureAdminAsync(int userId)
        {
            using var db = _factory.CreateDbContext();
            var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId);
            if (actor == null || actor.RoleId != RoleId.Admin)
                throw new UnauthorizedAccessException("Bu işlem için yetkiniz yok.");
        }

        /// <summary>
        /// Brick/ST eşleştirme haritasına hatırlanan NameAlias kayıtlarını ekler (gerçek adlar önce
        /// eklendiğinden TryAdd ile EZİLMEZ). Tablo yoksa (db/name-alias.sql çalışmamış) sessiz geçer.
        /// </summary>
        private static async Task MergeNameAliasesAsync(AppDbContext db, Dictionary<string, int> map, string aliasType)
        {
            try
            {
                var aliases = await db.NameAliases.AsNoTracking()
                    .Where(a => a.AliasType == aliasType).ToListAsync();
                foreach (var a in aliases) map.TryAdd(Normalize(a.AliasKey), a.TargetId);
            }
            catch { /* NameAliases tablosu yok — sessiz geç */ }
        }

        /// <summary>
        /// Önizlemedeki bir öneriyi KALICI eşlemeye çevirir (admin). Brick/ST için NameAlias upsert;
        /// Ürün için kanonik ürünün ImportAlias'ına ekler. Sonraki importlar otomatik çözer.
        /// </summary>
        public async Task SaveMatchAliasAsync(string aliasType, string sourceValue, int targetId, int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            var key = Normalize(sourceValue);
            if (key.Length == 0) throw new ArgumentException("Boş kaynak değer eşlenemez.");
            using var db = _factory.CreateDbContext();

            if (aliasType == "Product")
            {
                var product = await db.Products.FirstOrDefaultAsync(p => p.ProductId == targetId)
                    ?? throw new InvalidOperationException("Ürün bulunamadı.");
                var aliases = SplitAliases(product.ImportAlias).ToList();
                if (!aliases.Contains(key)) aliases.Add(key);
                product.ImportAlias = string.Join(";", aliases);
            }
            else // Brick | User
            {
                var hasTable = await db.Database.SqlQueryRaw<int>(
                    "SELECT CAST(CASE WHEN OBJECT_ID('dbo.NameAliases','U') IS NOT NULL THEN 1 ELSE 0 END AS int) AS Value")
                    .FirstAsync();
                if (hasTable == 0)
                    throw new InvalidOperationException("NameAliases tablosu yok — önce db/name-alias.sql çalıştırın.");

                var existing = await db.NameAliases
                    .FirstOrDefaultAsync(a => a.AliasType == aliasType && a.AliasKey == key);
                if (existing == null)
                    db.NameAliases.Add(new NameAlias { AliasType = aliasType, AliasKey = key, TargetId = targetId, CreatedBy = actorUserId });
                else
                    existing.TargetId = targetId;
            }

            await db.SaveChangesAsync();
            await _audit.LogAsync(actorUserId, "MatchAlias", aliasType, targetId, newValue: $"{key} → #{targetId}");
        }

        /// <summary>
        /// 106k+ SellOutDetail satırını EF yerine SqlBulkCopy ile aynı transaction'a yazar (~20s → ~1-2s).
        /// IDENTITY (SellOutDetailId) hariç tüm kolonlar haritalanır; null'lar DBNull. Accuracy birebir aynı.
        /// </summary>
        private static async Task BulkInsertSellOutDetailsAsync(AppDbContext db, IDbContextTransaction tx, List<SellOutDetail> rows)
        {
            var table = new DataTable();
            table.Columns.Add("BrickId", typeof(int));
            table.Columns.Add("ProductId", typeof(int));
            table.Columns.Add("PeriodId", typeof(int));
            table.Columns.Add("GLN", typeof(string));
            table.Columns.Add("KurumName", typeof(string));
            table.Columns.Add("Il", typeof(string));
            table.Columns.Add("Ilce", typeof(string));
            table.Columns.Add("Quantity", typeof(decimal));
            table.Columns.Add("AmountTL", typeof(decimal));
            table.Columns.Add("MFTL", typeof(decimal));
            table.Columns.Add("UserId", typeof(int));
            table.Columns.Add("ImportedAt", typeof(DateTime));
            foreach (var d in rows)
                table.Rows.Add(d.BrickId, d.ProductId, d.PeriodId, d.GLN,
                    (object?)d.KurumName ?? DBNull.Value, (object?)d.Il ?? DBNull.Value, (object?)d.Ilce ?? DBNull.Value,
                    d.Quantity, (object?)d.AmountTL ?? DBNull.Value, (object?)d.MFTL ?? DBNull.Value,
                    (object?)d.UserId ?? DBNull.Value, d.ImportedAt);

            var conn = (SqlConnection)db.Database.GetDbConnection();
            var sqlTx = (SqlTransaction)tx.GetDbTransaction();
            using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, sqlTx)
            {
                DestinationTableName = "dbo.SellOutDetails",
                BatchSize = 5000,
                BulkCopyTimeout = 180
            };
            foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            await bulk.WriteToServerAsync(table);
        }

        /// <summary>
        /// Sell-out GLN detaylarından Eczane (Institution) master'ını GLN bazında upsert eder.
        /// Yoksa ekler; varsa Name/City/Ilce/LastSeen günceller, BrickId yalnız admin override
        /// yoksa (BrickOverride=false). GLN boş/"-" atlanır. Tablo veya yeni kolonlar yoksa
        /// (db/institutions.sql çalışmamış) sessiz geçer — sell-out importu ETKİLENMEZ.
        /// </summary>
        private async Task UpsertInstitutionsFromDetailAsync(List<SellOutDetail> details, DateTime batchTime)
        {
            try
            {
                var byGln = new Dictionary<string, SellOutDetail>();
                foreach (var d in details)
                {
                    if (string.IsNullOrWhiteSpace(d.GLN) || d.GLN == "-") continue;
                    byGln[d.GLN.Trim()] = d;   // son görülen kazanır
                }
                if (byGln.Count == 0) return;

                // Satır-satır EF upsert yerine: distinct GLN'leri #temp'e SqlBulkCopy + tek MERGE (set-temelli, hızlı).
                using var db = _factory.CreateDbContext();
                var conn = (SqlConnection)db.Database.GetDbConnection();
                await conn.OpenAsync();
                try
                {
                    using (var create = conn.CreateCommand())
                    {
                        create.CommandText = @"CREATE TABLE #ins (
                            GLN nvarchar(450) NOT NULL, KurumName nvarchar(max) NULL,
                            City nvarchar(max) NULL, Ilce nvarchar(max) NULL, BrickId int NOT NULL);";
                        await create.ExecuteNonQueryAsync();
                    }

                    var table = new DataTable();
                    table.Columns.Add("GLN", typeof(string));
                    table.Columns.Add("KurumName", typeof(string));
                    table.Columns.Add("City", typeof(string));
                    table.Columns.Add("Ilce", typeof(string));
                    table.Columns.Add("BrickId", typeof(int));
                    foreach (var (gln, d) in byGln)
                        table.Rows.Add(gln, (object?)d.KurumName ?? DBNull.Value, (object?)d.Il ?? DBNull.Value,
                            (object?)d.Ilce ?? DBNull.Value, d.BrickId);
                    using (var bulk = new SqlBulkCopy(conn) { DestinationTableName = "#ins", BatchSize = 5000, BulkCopyTimeout = 120 })
                    {
                        foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
                        await bulk.WriteToServerAsync(table);
                    }

                    using (var merge = conn.CreateCommand())
                    {
                        merge.CommandText = @"
                            MERGE dbo.Institutions AS t
                            USING #ins AS s ON t.GLN = s.GLN
                            WHEN MATCHED THEN UPDATE SET
                                t.Name   = CASE WHEN NULLIF(LTRIM(RTRIM(s.KurumName)),'') IS NOT NULL THEN s.KurumName ELSE t.Name END,
                                t.City   = CASE WHEN NULLIF(LTRIM(RTRIM(s.City)),'')    IS NOT NULL THEN s.City    ELSE t.City END,
                                t.Ilce   = CASE WHEN NULLIF(LTRIM(RTRIM(s.Ilce)),'')    IS NOT NULL THEN s.Ilce    ELSE t.Ilce END,
                                t.BrickId = CASE WHEN t.BrickOverride = 0 THEN s.BrickId ELSE t.BrickId END,
                                t.LastSeenAt = @batch, t.IsActive = 1
                            WHEN NOT MATCHED THEN
                                INSERT (GLN, Name, City, Ilce, BrickId, Segment, BrickOverride, IsActive, CreatedAt, LastSeenAt)
                                VALUES (s.GLN, ISNULL(NULLIF(LTRIM(RTRIM(s.KurumName)),''), s.GLN), s.City, s.Ilce, s.BrickId, NULL, 0, 1, @batch, @batch);";
                        var bp = merge.CreateParameter(); bp.ParameterName = "@batch"; bp.Value = batchTime; merge.Parameters.Add(bp);
                        await merge.ExecuteNonQueryAsync();
                    }
                }
                finally { await conn.CloseAsync(); }
            }
            catch { /* Institutions tablosu/yeni kolonları yok — best-effort, import etkilenmez */ }
        }

        /// <summary>Admin: bir eczanenin (Institution) brick'ini elle düzeltir (override kalıcı).</summary>
        public async Task SaveInstitutionBrickAsync(int institutionId, int brickId, int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            using var db = _factory.CreateDbContext();
            var inst = await db.Institutions.FirstOrDefaultAsync(i => i.InstitutionId == institutionId)
                ?? throw new InvalidOperationException("Eczane bulunamadı.");
            inst.BrickId = brickId;
            inst.BrickOverride = true;
            await db.SaveChangesAsync();
            await _audit.LogAsync(actorUserId, "InstitutionBrick", "Institution", institutionId, newValue: $"Brick={brickId}");
        }

        // ── Yükleme Kütüphanesi (ImportBatch) ────────────────────────────────

        /// <summary>ImportBatches tablosu var mı (db/import-batch.sql çalıştırılmış mı).</summary>
        private static async Task<bool> BatchTableExistsAsync(AppDbContext db)
            => await db.Database.SqlQueryRaw<int>(
                "SELECT CAST(CASE WHEN OBJECT_ID('dbo.ImportBatches','U') IS NOT NULL THEN 1 ELSE 0 END AS int) AS Value")
                .FirstAsync() == 1;

        /// <summary>
        /// Başarılı bir yüklemeyi kütüphaneye kaydeder (ham bayt + meta). Aynı FileType'ın önceki
        /// aktif batch'leri pasifleştirilir (her tip için tek aktif). Tablo yoksa sessiz geçer
        /// (özellik opsiyonel — db/import-batch.sql çalışmamış olabilir, yükleme yine de tamam).
        /// </summary>
        public async Task RegisterBatchAsync(string fileType, string fileName, string? fileHash,
            byte[] content, string mode, int? year, string? quarter, int rowCount, decimal? qtyTotal, int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            try
            {
                using var db = _factory.CreateDbContext();
                if (!await BatchTableExistsAsync(db)) return;          // tablo yoksa sessiz geç (ERR log üretme)
                var prior = await db.ImportBatches.Where(b => b.FileType == fileType && b.IsActive).ToListAsync();
                foreach (var p in prior) p.IsActive = false;
                db.ImportBatches.Add(new ImportBatch
                {
                    FileType = fileType, FileName = fileName, FileHash = fileHash, Content = content,
                    Mode = mode, Year = year, Quarter = quarter, IsActive = true,
                    RowCount = rowCount, QtyTotal = qtyTotal, UploadedById = actorUserId, Status = "Active"
                });
                await db.SaveChangesAsync();
            }
            catch { /* ImportBatches tablosu yok — kütüphane devre dışı, yükleme etkilenmez */ }
        }

        /// <summary>Kütüphane listesi (ham içerik HARİÇ — büyük). Tablo yoksa boş döner.</summary>
        public async Task<List<ImportBatch>> GetBatchesAsync(string? fileType, int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            try
            {
                using var db = _factory.CreateDbContext();
                if (!await BatchTableExistsAsync(db)) return new();   // tablo yoksa ERR log üretme
                var q = db.ImportBatches.AsNoTracking().AsQueryable();
                if (!string.IsNullOrEmpty(fileType)) q = q.Where(b => b.FileType == fileType);
                return await q.OrderByDescending(b => b.UploadedAt)
                    .Select(b => new ImportBatch
                    {
                        BatchId = b.BatchId, FileType = b.FileType, FileName = b.FileName, FileHash = b.FileHash,
                        Mode = b.Mode, Year = b.Year, Quarter = b.Quarter, IsActive = b.IsActive,
                        RowCount = b.RowCount, QtyTotal = b.QtyTotal, UploadedAt = b.UploadedAt,
                        UploadedById = b.UploadedById, Status = b.Status, Content = Array.Empty<byte>()
                    }).ToListAsync();
            }
            catch { return new(); }
        }

        /// <summary>
        /// Geçmiş bir dosyayı yeniden aktifleştirir: ham baytı saklı içerikten okuyup ilgili import
        /// metodunu batch'in moduyla (Replace/Append) yeniden çalıştırır, sonra aktif bayrağını çevirir.
        /// </summary>
        public async Task<ImportResult> ReactivateBatchAsync(int batchId, int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            byte[] content; string fileType, fileName, mode; int? year; string? quarter;
            using (var db = _factory.CreateDbContext())
            {
                if (!await BatchTableExistsAsync(db))
                    throw new InvalidOperationException("Yükleme Kütüphanesi tablosu yok — önce db/import-batch.sql çalıştırın.");
                var b = await db.ImportBatches.FirstOrDefaultAsync(x => x.BatchId == batchId)
                    ?? throw new InvalidOperationException("Yükleme bulunamadı.");
                content = b.Content; fileType = b.FileType; fileName = b.FileName;
                mode = b.Mode; year = b.Year; quarter = b.Quarter;
            }

            bool replace = mode == "Replace";
            using var ms = new MemoryStream(content);
            ImportResult result = fileType switch
            {
                "sellout"    => await ImportSellOutAsync(ms, fileName, actorUserId, replace, false),
                "order"      => await ImportOrdersAsync(ms, fileName, actorUserId, replace, false),
                "target"     => await ImportTargetsAsync(ms, fileName, actorUserId, year ?? 0, replace, false),
                "ruta"       => await ImportRutaAsync(ms, fileName, actorUserId, year ?? 0, quarter ?? "", replace, false),
                "visitplan"  => await ImportVisitPlanAsync(ms, fileName, actorUserId, year ?? 0, quarter ?? "", false),
                "pharmacyplan" => await ImportPharmacyPlanAsync(ms, fileName, actorUserId, year ?? 0, replace, false),
                "productmap" => await ImportProductMapAsync(ms, fileName, actorUserId, false),
                "brickmap"   => await ImportBrickAssignmentsAsync(ms, fileName, actorUserId, replace, false),
                _            => throw new InvalidOperationException("Bilinmeyen tip: " + fileType)
            };

            if (result.Success)
            {
                using var db = _factory.CreateDbContext();
                var batches = await db.ImportBatches.Where(x => x.FileType == fileType).ToListAsync();
                foreach (var x in batches) x.IsActive = x.BatchId == batchId;
                await db.SaveChangesAsync();
                await _audit.LogAsync(actorUserId, "ReactivateBatch", fileType, batchId, newValue: fileName);
            }
            return result;
        }

        /// <summary>Kütüphaneden bir kaydı siler (yalnız meta+ham içerik; materyalize veriyi etkilemez).</summary>
        public async Task DeleteBatchAsync(int batchId, int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            using var db = _factory.CreateDbContext();
            if (!await BatchTableExistsAsync(db)) return;
            var b = await db.ImportBatches.FirstOrDefaultAsync(x => x.BatchId == batchId);
            if (b == null) return;
            db.ImportBatches.Remove(b);
            await db.SaveChangesAsync();
            await _audit.LogAsync(actorUserId, "DeleteBatch", b.FileType, batchId, newValue: b.FileName);
        }

        // ── Ortak sağlamlık yardımcıları ─────────────────────────────────────

        /// <summary>
        /// Başlık satırını beklenen kolon adlarına karşı doğrular. Yanlış panele yanlış
        /// dosya seçilmesini yakalamak içindir; başlık metni varyasyonlarına tolerans için
        /// en az 2 beklenen adın eşleşmesi yeterli sayılır (içerme, büyük/küçük duyarsız).
        /// </summary>
        internal static string? ValidateHeader(ExcelWorksheet ws, int headerRow, (int Col, string Expected)[] cols, string tip, int minMatch = 2)
        {
            int matched = 0;
            var found = new List<string>();
            foreach (var (col, expected) in cols)
            {
                var text = ws.Cells[headerRow, col].Text?.Trim() ?? "";
                found.Add(string.IsNullOrEmpty(text) ? "(boş)" : text);
                if (text.ToUpperInvariant().Contains(expected.ToUpperInvariant())) matched++;
            }
            if (matched < minMatch)
                return $"Dosya '{tip}' şablonuna uymuyor (başlık satırı {headerRow}). " +
                       $"Beklenen kolonlar: {string.Join(" · ", cols.Select(c => c.Expected))} — " +
                       $"Dosyada bulunan: {string.Join(" · ", found)}. Doğru paneli/dosyayı seçtiğinizden emin olun.";
            return null;
        }

        /// <summary>İçe aktarma istatistiklerinden ImportLog durumunu türetir.</summary>
        internal static string ClassifyStatus(int skipped, int errorCount) =>
            skipped > 0 || errorCount > 0 ? "Partial" : "Success";

        /// <summary>
        /// DB ve dosya miktar toplamı "tutuyor mu". Tam-eşitlik (==) yerine satır sayısıyla ölçeklenen
        /// küçük tolerans: <c>TargetQty</c> gibi <c>decimal(18,4)</c> kolonlarda DB değeri yuvarlanır,
        /// FileTotal ise tam-hassas bellek toplamıdır → ~0,5 birim fark yanlış-negatif veriyordu.
        /// Gerçek tutmazlık yüzlerce/binlerce birim olur; bu tolerans onu yine yakalar.
        /// </summary>
        internal static bool AmountsVerified(decimal? db, decimal? file, int rowCount)
            => Math.Abs((db ?? 0m) - (file ?? 0m)) <= Math.Max(1m, rowCount * 0.0001m);

        /// <summary>
        /// Başlık satırını ADINA göre tarayıp her mantıksal alan için 1-tabanlı kolon index'i döndürür.
        /// Sabit konum yerine ad eşlemesi → araya giren fazladan kolon (örn. "ST") veya kolon sırası
        /// değişikliği otomatik tolere edilir. Bulunamayan alan sözlükte yer almaz. Eşleme Normalize
        /// üzerinden (büyük/küçük + Türkçe İ/ı duyarsız). İlk eşleşen kolon kazanır.
        /// </summary>
        internal static Dictionary<string, int> BuildColumnMap(
            ExcelWorksheet ws, int headerRow, (string Field, Func<string, bool> Match)[] specs)
        {
            int lastCol = ws.Dimension?.Columns ?? 0;
            var headers = new List<(int Col, string Norm)>();
            for (int c = 1; c <= lastCol; c++)
                headers.Add((c, Normalize(ws.Cells[headerRow, c].Text)));

            var map = new Dictionary<string, int>();
            foreach (var (field, match) in specs)
                foreach (var (col, norm) in headers)
                    if (norm.Length > 0 && match(norm)) { map[field] = col; break; }
            return map;
        }

        /// <summary>
        /// Hücre sayısal değerini biçim/kültür duyarsız okur. EPPlus'ın ham <c>.Value</c>'su
        /// sayısalsa DOĞRUDAN kullanılır — binlik/ondalık ayraç ve hücre biçimi (örn. muhasebe
        /// <c>#,##0</c>) sorunu hiç oluşmaz. Yalnız değer metinse hem tr-TR (.=binlik, ,=ondalık)
        /// hem InvariantCulture denenir; son çare ayraçları normalize eder. Boş → null
        /// ("yok" anlamı); dolu ama çözülemiyor → null (çağıran sessiz 0 yerine HATA raporlamalı).
        /// Önceki kod <c>.Text</c> (biçimli metin) okuyup naïf <c>Replace(",",".")</c>+Invariant
        /// kullandığından "1.538" → 1,538 gibi ≥1000 değerleri bozuyordu; bu yardımcı onu giderir.
        /// </summary>
        internal static decimal? ParseCellNumber(object? rawValue, string? text)
        {
            switch (rawValue)
            {
                case double d:  return (decimal)d;
                case decimal m: return m;
                case int i:     return i;
                case long l:    return l;
                case float f:   return (decimal)f;
            }

            var s = (text ?? rawValue?.ToString())?.Trim();
            if (string.IsNullOrEmpty(s)) return null;

            var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, tr, out var vtr)) return vtr;
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var vinv)) return vinv;

            // Son çare: rakam ve ayraçları al, son ayracı ondalık kabul et, gerisini sök.
            var kept = new string(s.Where(c => char.IsDigit(c) || c == '.' || c == ',' || c == '-').ToArray());
            bool neg = kept.StartsWith("-");
            int lastSep = Math.Max(kept.LastIndexOf('.'), kept.LastIndexOf(','));
            if (lastSep >= 0)
            {
                var intPart = new string(kept.Substring(0, lastSep).Where(char.IsDigit).ToArray());
                var fracPart = new string(kept.Substring(lastSep + 1).Where(char.IsDigit).ToArray());
                var normalized = (intPart.Length == 0 ? "0" : intPart) +
                                 (fracPart.Length == 0 ? "" : "." + fracPart);
                if (neg) normalized = "-" + normalized;
                if (decimal.TryParse(normalized, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var vlast)) return vlast;
            }
            return null;
        }

        /// <summary>Worksheet hücresini ham .Value + .Text birlikte vererek sağlam okur.</summary>
        private static decimal? ReadNum(ExcelWorksheet ws, int row, int col)
            => ParseCellNumber(ws.Cells[row, col].Value, ws.Cells[row, col].Text);

        // Stream'i belleğe alır ve SHA256 hash üretir (çift yükleme tespiti için).
        private static async Task<(byte[] Bytes, string Hash)> ReadAllAsync(Stream s)
        {
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            var bytes = ms.ToArray();
            return (bytes, Convert.ToHexString(SHA256.HashData(bytes)));
        }

        // Aynı tip + aynı içerikli dosya daha önce başarıyla yüklendiyse tarihini döndürür.
        private static async Task<DateTime?> FindPreviousImportAsync(AppDbContext db, string fileType, string fileHash)
        {
            return await db.ImportLogs
                .Where(l => l.FileType == fileType && l.FileHash == fileHash && l.Status != "Failed")
                .OrderByDescending(l => l.ImportedAt)
                .Select(l => (DateTime?)l.ImportedAt)
                .FirstOrDefaultAsync();
        }

        // Exception durumunda bile iz bırak — ayrı context, hata yutma bilinçli (log'un log'u olmaz).
        private async Task TryLogFailureAsync(int userId, string fileType, string fileName, string error, DateTime batchTime)
        {
            try
            {
                using var db = _factory.CreateDbContext();
                db.ImportLogs.Add(new ImportLog
                {
                    UserId = userId, FileType = fileType, FileName = fileName,
                    Status = "Failed",
                    ErrorMsg = error.Length > 500 ? error[..500] : error,
                    ImportedAt = batchTime
                });
                await db.SaveChangesAsync();
            }
            catch { /* loglama hatası asıl hatayı gölgelemesin */ }
        }

        private static string? SerializeErrors(List<string> errors) =>
            errors.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(errors.Take(100));

        // Brick→ST haritasını (UserBricks) verilen brickToUser sözlüğüne göre senkronlar.
        // "brick → tek aktif ST" invariantını uygular: bir brick farklı ST'de aktifse pasifler,
        // hedef ST'de yoksa ekler / pasifse yeniden aktifler. Böylece hedef ve sell-out aynı
        // brick haritasını kullanır (atıf çakışması engellenir). apply=false yalnız sayar
        // (dry-run önizlemesi). Çağıran transaction içinde apply:true ile uygular.
        private static async Task<(int Synced, int Deactivated)> SyncBrickAssignmentsAsync(
            AppDbContext db, Dictionary<int, int> brickToUser, DateTime batchTime, bool apply)
        {
            if (brickToUser.Count == 0) return (0, 0);
            var brickIds = brickToUser.Keys.ToList();
            var existing = await db.UserBricks
                .Where(ub => brickIds.Contains(ub.BrickId))
                .ToListAsync();
            int synced = 0, deactivated = 0;
            foreach (var (brickId, userId) in brickToUser)
            {
                // Aynı brick'in farklı ST'deki aktif atamalarını pasifle (brick→tek aktif ST).
                foreach (var other in existing.Where(ub => ub.BrickId == brickId && ub.UserId != userId && ub.IsActive))
                {
                    if (apply) { other.IsActive = false; other.UnassignedAt = batchTime; }
                    deactivated++;
                }
                var mine = existing.FirstOrDefault(ub => ub.BrickId == brickId && ub.UserId == userId);
                if (mine == null)
                {
                    if (apply) db.UserBricks.Add(new UserBrick { UserId = userId, BrickId = brickId, IsActive = true, AssignedAt = batchTime });
                    synced++;
                }
                else if (!mine.IsActive)
                {
                    if (apply) { mine.IsActive = true; mine.UnassignedAt = null; mine.AssignedAt = batchTime; }
                    synced++;
                }
                // zaten aktif + doğru ST → değişiklik yok
            }
            return (synced, deactivated);
        }

        // ============================================================
        // SELL-OUT IMPORT (InfiniLens DistInvTran — YENİ 11-kolon GLN formatı)
        // Satır 1: boş, Satır 2: header, Satır 3+: data
        // Sütunlar: Yıl(1) | Ay(2,YYYY-MM) | İl(3) | İlçe(4) | Brick(5) | Kurum GLN(6) |
        //           Kurum(7) | Marka(8) | Toplam KUTU(9) | Ana Mal TL(10) | MF TL(11)
        // Aynı Brick+Marka+Dönem birden çok GLN satırına bölünür → değerler TOPLANIR
        // (SellOuts = brick toplamı, skor için). Ham GLN kırılımı SellOutDetails'e yazılır.
        // ============================================================
        public async Task<ImportResult> ImportSellOutAsync(Stream fileStream, string fileName, int userId, bool replaceExisting = false, bool dryRun = false)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var result = new ImportResult { FileName = fileName, FileType = "SellOut", IsDryRun = dryRun };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Tek batch zaman damgası — tüm satırlar + ImportLog aynı değeri taşır,
            // böylece rollback ±60sn pencere yerine tam eşleşme ile güvenli çalışır.
            var batchTime = DateTime.UtcNow;

            try
            {
                var (bytes, fileHash) = await ReadAllAsync(fileStream);
                result.FileHash = fileHash;
                result.DuplicateOf = await FindPreviousImportAsync(db, "SellOut", fileHash);

                using var package = new ExcelPackage(new MemoryStream(bytes));
                var ws = package.Workbook.Worksheets[0];
                var rowCount = ws.Dimension?.Rows ?? 0;
                if (rowCount < 3) { result.Error = "Dosya boş veya hatalı format"; return result; }

                // Kolonları ADINA göre eşle (sabit konum DEĞİL) — araya giren "ST" gibi fazladan
                // kolonlar ve sıra değişiklikleri otomatik tolere edilir. Başlık satırı 2.
                var colMap = BuildColumnMap(ws, 2, new (string, Func<string, bool>)[]
                {
                    ("Ay",       h => h == "AY"),
                    ("Brick",    h => h.Contains("BRICK")),
                    ("GLN",      h => h.Contains("GLN")),
                    ("Kurum",    h => h == "KURUM"),
                    ("Marka",    h => h.Contains("MARKA")),
                    ("Kutu",     h => h.Contains("KUTU")),
                    ("AnaMalTL", h => h.Contains("ANA MAL")),
                    ("MFTL",     h => h.Contains("MF")),
                    ("Il",       h => h == "IL"),
                    ("Ilce",     h => h.Contains("ILÇE") || h.Contains("ILCE")),
                });
                var missingCols = new[] { "Ay", "Brick", "Marka", "Kutu" }.Where(f => !colMap.ContainsKey(f)).ToList();
                if (missingCols.Any())
                {
                    result.Error = $"Dosya 'Sell-Out' şablonuna uymuyor — zorunlu kolon başlıkları bulunamadı: " +
                                   $"{string.Join(", ", missingCols)}. Beklenen başlıklar (satır 2): " +
                                   "Ay · Brick · Marka · Toplam KUTU (Kurum GLN/Kurum/TL kolonları opsiyonel, sıra/ek kolon serbest).";
                    return result;
                }
                int cAy = colMap["Ay"], cBrick = colMap["Brick"], cMarka = colMap["Marka"], cKutu = colMap["Kutu"];
                colMap.TryGetValue("GLN", out int cGln);
                colMap.TryGetValue("Kurum", out int cKurum);
                colMap.TryGetValue("AnaMalTL", out int cTl);
                colMap.TryGetValue("MFTL", out int cMf);
                colMap.TryGetValue("Il", out int cIl);
                colMap.TryGetValue("Ilce", out int cIlce);

                // Şema hazır mı? db/sellout-gln.sql çalıştırılmamışsa kriptik SQL hatası yerine
                // net uyarı (dry-run önizlemede de görünür, 111k satır parse edilmeden önce).
                var detailTableExists = await db.Database
                    .SqlQueryRaw<int>("SELECT CAST(CASE WHEN OBJECT_ID('dbo.SellOutDetails','U') IS NOT NULL THEN 1 ELSE 0 END AS int) AS Value")
                    .FirstAsync();
                if (detailTableExists == 0)
                {
                    result.Error = "SellOutDetails tablosu yok — 'db/sellout-gln.sql' veritabanında çalıştırılmamış. Scripti çalıştırıp tekrar deneyin.";
                    return result;
                }

                // Ürün eşleşmesi: Name + (çoklu) ImportAlias — hedef importuyla aynı mantık.
                var productList = await db.Products
                    .Where(p => p.IsActive)
                    .Select(p => new { p.ProductId, p.Name, p.ImportAlias })
                    .ToListAsync();
                var productMap = BuildProductMap(productList.Select(p => (p.ProductId, p.Name, p.ImportAlias)));

                var brickMap = await db.Bricks
                    .Where(b => b.IsActive)
                    .ToDictionaryAsync(b => Normalize(b.Name), b => b.BrickId);
                await MergeNameAliasesAsync(db, brickMap, "Brick");

                var userBrickMap = await db.UserBricks
                    .Where(ub => ub.IsActive)
                    .GroupBy(ub => ub.BrickId)
                    .Select(g => new { BrickId = g.Key, UserId = g.First().UserId })
                    .ToDictionaryAsync(x => x.BrickId, x => x.UserId);

                var periodMap = await db.Periods
                    .ToDictionaryAsync(p => $"{p.Year}-{p.Month:D2}", p => p.PeriodId);

                // Tüm kullanılan aralığı TEK çağrıyla belleğe al — per-hücre EPPlus erişimi yerine
                // (okuma ~10-30× hızlanır). Değerler ham .Value; ParseCellNumber/Normalize accuracy korur.
                var dim = ws.Dimension!;
                int r0 = dim.Start.Row, c0 = dim.Start.Column;
                var data = (object[,])ws.Cells[dim.Address].Value;
                int dRows = data.GetLength(0), dCols = data.GetLength(1);
                object? CellAt(int row, int col)
                {
                    int rr = row - r0, cc = col - c0;
                    return (rr >= 0 && cc >= 0 && rr < dRows && cc < dCols) ? data[rr, cc] : null;
                }
                string? CellStr(int row, int col) => CellAt(row, col)?.ToString()?.Trim();

                // Dosyadaki dönemleri (replace silme + dedup yüklemesi için) bellekteki diziden tara.
                var filePeriodIds = new HashSet<int>();
                for (int r = 3; r <= rowCount; r++)
                {
                    var monthStr = CellStr(r, cAy);
                    if (!string.IsNullOrEmpty(monthStr) && monthStr.Contains('-'))
                    {
                        var parts = monthStr.Split('-');
                        if (int.TryParse(parts[0], out int y) && int.TryParse(parts[1], out int m))
                        {
                            var pk = $"{y}-{m:D2}";
                            if (periodMap.TryGetValue(pk, out var pid)) filePeriodIds.Add(pid);
                        }
                    }
                }

                // Dedup için yalnızca dosyadaki dönemlere ait kayıtları yükle (tüm tabloyu değil).
                // Replace modunda harita boş başlar — silme yazma aşamasında transaction içinde yapılır.
                var existingSellOuts = replaceExisting
                    ? new List<SellOut>()
                    : await db.SellOuts.Where(s => filePeriodIds.Contains(s.PeriodId)).ToListAsync();
                var existingMap = existingSellOuts
                    .GroupBy(s => $"{s.BrickId}_{s.ProductId}_{s.PeriodId}")
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.ImportedAt).First());

                var toInsert = new List<SellOut>();
                var toInsertDetail = new List<SellOutDetail>();
                var errors = new List<string>();
                var unmatchedBricks = new HashSet<string>();
                var unmatchedProducts = new HashSet<string>();
                var unmatchedPeriods = new HashSet<string>();
                // Kapsam (dry-run "double-check" paneli): dosyada görülen distinct değerler
                var seenBrands = new HashSet<string>();
                var seenBricks = new HashSet<string>();
                var seenPeriods = new HashSet<string>();
                // Aynı dosyada tekrar eden (brick+marka+dönem) GLN satırları TOPLANIR;
                // önceki import'tan gelen kayıt ise dosyadaki ilk satırla değiştirilir.
                var touchedThisBatch = new HashSet<string>();
                int skipped = 0, updated = 0, detailCount = 0;
                decimal fileTotal = 0m; // işlenen satırların toplam kutu adedi (teyit için)

                for (int row = 3; row <= rowCount; row++)
                {
                    try
                    {
                        var monthStr  = CellStr(row, cAy);
                        var il        = cIl   > 0 ? CellStr(row, cIl)   : null;
                        var ilce      = cIlce > 0 ? CellStr(row, cIlce) : null;
                        var brickName = Normalize(CellStr(row, cBrick));
                        var gln       = cGln   > 0 ? CellStr(row, cGln)   : null;
                        var kurum     = cKurum > 0 ? CellStr(row, cKurum) : null;
                        var marka     = Normalize(CellStr(row, cMarka));

                        if (!string.IsNullOrEmpty(marka)) seenBrands.Add(marka!);

                        if (string.IsNullOrEmpty(brickName) || brickName == "-" || brickName.Contains("BRICK ATANMAMI"))
                        { skipped++; continue; }
                        if (string.IsNullOrEmpty(marka)) { skipped++; continue; }

                        int year = 0, month = 0;
                        if (!string.IsNullOrEmpty(monthStr) && monthStr.Contains('-'))
                        {
                            var parts = monthStr.Split('-');
                            int.TryParse(parts[0], out year);
                            int.TryParse(parts[1], out month);
                        }
                        if (year == 0 || month == 0) { skipped++; continue; }

                        var periodKey = $"{year}-{month:D2}";
                        seenPeriods.Add(periodKey);
                        seenBricks.Add(brickName!);

                        if (!periodMap.ContainsKey(periodKey)) { unmatchedPeriods.Add(periodKey); skipped++; continue; }
                        if (!brickMap.ContainsKey(brickName)) { unmatchedBricks.Add(brickName!); skipped++; continue; }
                        if (!productMap.ContainsKey(marka)) { unmatchedProducts.Add(marka!); skipped++; continue; }

                        decimal kutu = ParseCellNumber(CellAt(row, cKutu), null) ?? 0m;
                        decimal tl   = cTl > 0 ? (ParseCellNumber(CellAt(row, cTl), null) ?? 0m) : 0m;
                        decimal mf   = cMf > 0 ? (ParseCellNumber(CellAt(row, cMf), null) ?? 0m) : 0m;

                        // Negatif değerler = İADE/düzeltme — net sell-out'a (satış − iade) dahil
                        // edilir; satırlar TOPLANIRken iade pozitif satışı düşürür. Atlanmaz.

                        var brickId = brickMap[brickName];
                        var productId = productMap[marka];
                        var periodId = periodMap[periodKey];
                        int? stUserId = userBrickMap.ContainsKey(brickId) ? userBrickMap[brickId] : null;
                        var key = $"{brickId}_{productId}_{periodId}";

                        if (existingMap.TryGetValue(key, out var existing))
                        {
                            if (touchedThisBatch.Contains(key))
                            {
                                // Bu dosyada aynı anahtar (başka GLN) zaten işlendi → TOPLA
                                existing.Quantity += kutu;
                                existing.AmountTL = (existing.AmountTL ?? 0m) + tl;
                                existing.MFTL = (existing.MFTL ?? 0m) + mf;
                            }
                            else
                            {
                                // Önceki import'tan kalan kayıt → dosyadaki ilk değerle değiştir
                                existing.Quantity = kutu;
                                existing.AmountTL = tl;
                                existing.MFTL = mf;
                                updated++;
                            }
                            existing.UserId = stUserId;
                            existing.SnapshotDate = batchTime;
                            existing.ImportedAt = batchTime;
                        }
                        else
                        {
                            var so = new SellOut
                            {
                                BrickId = brickId,
                                ProductId = productId,
                                PeriodId = periodId,
                                Quantity = kutu,
                                AmountTL = tl,
                                MFTL = mf,
                                UserId = stUserId,
                                SnapshotDate = batchTime,
                                ImportedAt = batchTime
                            };
                            toInsert.Add(so);
                            existingMap[key] = so;
                        }
                        touchedThisBatch.Add(key);

                        // Ham GLN/kurum detayı (her satır birebir saklanır). Dry-run'da nesne kurulmaz
                        // (yalnız sayılır) — önizleme parse'ı hafifler; gerçek import'ta toplu yazılır.
                        detailCount++;
                        if (!dryRun)
                            toInsertDetail.Add(new SellOutDetail
                            {
                                BrickId = brickId,
                                ProductId = productId,
                                PeriodId = periodId,
                                GLN = string.IsNullOrEmpty(gln) ? "-" : gln!,
                                KurumName = string.IsNullOrEmpty(kurum) ? null : kurum,
                                Il = string.IsNullOrEmpty(il) ? null : il,
                                Ilce = string.IsNullOrEmpty(ilce) ? null : ilce,
                                Quantity = kutu,
                                AmountTL = tl,
                                MFTL = mf,
                                UserId = stUserId,
                                ImportedAt = batchTime
                            });

                        fileTotal += kutu;
                    }
                    catch (Exception ex) { errors.Add($"Satır {row}: {ex.Message}"); skipped++; }
                }

                AppendUnmatched(errors, "Brick", unmatchedBricks);
                AppendUnmatched(errors, "Ürün", unmatchedProducts);
                AppendUnmatched(errors, "Dönem", unmatchedPeriods);
                if (dryRun)
                {
                    AppendSuggestions(result.MatchSuggestions, "Brick", unmatchedBricks, brickMap);
                    AppendSuggestions(result.MatchSuggestions, "Product", unmatchedProducts, productMap);
                }

                sw.Stop();
                result.Success = true;
                result.TotalRows = rowCount - 2;
                result.InsertedRows = toInsert.Count;
                result.UpdatedRows = updated;
                result.SkippedRows = skipped;
                result.Errors = errors;
                result.ProcessingMs = (int)sw.ElapsedMilliseconds;
                result.FileTotal = fileTotal;
                result.ExpectedCount = toInsert.Count + updated;

                // Eşleşme kapsamı (double-check paneli)
                result.TotalBrandCount = seenBrands.Count;
                result.UnmatchedBrands = unmatchedProducts.OrderBy(x => x).ToList();
                result.MatchedBrandCount = seenBrands.Count - unmatchedProducts.Count;
                result.TotalBrickCount = seenBricks.Count;
                result.MatchedBrickCount = seenBricks.Count - unmatchedBricks.Count;
                result.TotalPeriodCount = seenPeriods.Count;
                result.MatchedPeriodCount = seenPeriods.Count - unmatchedPeriods.Count;
                result.DetailRowCount = detailCount;

                // Önizleme modu: hiçbir yazma yapılmaz
                if (dryRun) return result;

                // Yazma aşaması — silme + ekleme + log TEK transaction'da (kısmi veri kalmaz)
                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    if (filePeriodIds.Any())
                    {
                        // Detay tablo dosyanın dönemleri için TAM yenilenir (her import o dönemin
                        // yetkili GLN kırılımını taşır) — re-import'ta detay çoğalmaz.
                        await db.SellOutDetails.Where(d => filePeriodIds.Contains(d.PeriodId)).ExecuteDeleteAsync();
                        if (replaceExisting)
                            await db.SellOuts.Where(s => filePeriodIds.Contains(s.PeriodId)).ExecuteDeleteAsync();
                    }

                    if (toInsert.Any()) db.SellOuts.AddRange(toInsert);   // ~9,7k aggregate — EF (update yolu var)

                    db.ImportLogs.Add(new ImportLog
                    {
                        UserId = userId,
                        FileType = "SellOut",
                        FileName = fileName,
                        FileHash = fileHash,
                        ImportRowCount = toInsert.Count + updated,
                        ReplacedCount = updated,
                        Status = ClassifyStatus(skipped, errors.Count),
                        ValidationErrors = SerializeErrors(errors),
                        ProcessingTimeMs = result.ProcessingMs,
                        ImportedAt = batchTime
                    });
                    await db.SaveChangesAsync();   // SellOuts aggregate + ImportLog

                    // 106k ham GLN detayı: EF AddRange yerine SqlBulkCopy (AYNI transaction) → ~20s → ~1-2s.
                    if (toInsertDetail.Count > 0)
                        await BulkInsertSellOutDetailsAsync(db, tx, toInsertDetail);

                    await tx.CommitAsync();
                }

                // Eczane (Institution) master'ını GLN bazında güncelle — best-effort, ana import'tan
                // bağımsız (tablo/yeni kolonlar yoksa sessiz geçer, sell-out yine de yüklenmiştir).
                if (toInsertDetail.Count > 0)
                    await UpsertInstitutionsFromDetailAsync(toInsertDetail, batchTime);

                // Teyit: batch gerçekten DB'de mi? Satır sayısı + miktar toplamı karşılaştırılır.
                result.VerifiedCount = await db.SellOuts.CountAsync(s => s.ImportedAt == batchTime);
                result.DbTotal = await db.SellOuts.Where(s => s.ImportedAt == batchTime)
                    .SumAsync(s => (decimal?)s.Quantity) ?? 0m;
                result.Verified = result.VerifiedCount == result.ExpectedCount
                    && AmountsVerified(result.DbTotal, result.FileTotal, result.ExpectedCount);

                // Detay ↔ aggregate tutarlılığı: GLN detay toplamı = brick toplamı (= dosya toplamı)
                var detailTotal = await db.SellOutDetails.Where(d => d.ImportedAt == batchTime)
                    .SumAsync(d => (decimal?)d.Quantity) ?? 0m;
                result.DetailConsistent = detailTotal == result.DbTotal;

                await _audit.LogAsync(userId, "Import", "SellOut", null,
                    newValue: $"{fileName} — {result.InsertedRows}i/{result.UpdatedRows}g ({result.SkippedRows}atl) · {toInsertDetail.Count} GLN detay");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
                if (!dryRun) await TryLogFailureAsync(userId, "SellOut", fileName, ex.Message, batchTime);
            }

            return result;
        }

        // ============================================================
        // SİPARİŞ IMPORT (InfiniLens SalesOrder)
        // Satır 1: boş, Satır 2: header, Satır 3+: data
        // Sütunlar: Yıl | Ay | AnaDepo | Brick | Personel | Marka | ToplamKutu | AnaMalTL | MFTL | KurumAdedi
        // ============================================================
        public async Task<ImportResult> ImportOrdersAsync(Stream fileStream, string fileName, int userId, bool replaceExisting = false, bool dryRun = false)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var result = new ImportResult { FileName = fileName, FileType = "Sipariş", IsDryRun = dryRun };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var batchTime = DateTime.UtcNow;

            try
            {
                var (bytes, fileHash) = await ReadAllAsync(fileStream);
                result.FileHash = fileHash;
                result.DuplicateOf = await FindPreviousImportAsync(db, "Sipariş", fileHash);

                using var package = new ExcelPackage(new MemoryStream(bytes));
                var ws = package.Workbook.Worksheets[0];
                var rowCount = ws.Dimension?.Rows ?? 0;
                if (rowCount < 3) { result.Error = "Dosya boş veya hatalı format"; return result; }

                // Kolonları ADINA göre eşle (sabit konum değil) — fazladan/sırası değişmiş kolonlar tolere edilir.
                var colMap = BuildColumnMap(ws, 2, new (string, Func<string, bool>)[]
                {
                    ("Ay",       h => h == "AY"),
                    ("Depot",    h => h.Contains("DEPO")),
                    ("Brick",    h => h.Contains("BRICK")),
                    ("Personel", h => h.Contains("PERSONEL") || h.Contains("MÜMESS") || h.Contains("MUMESS") || h == "ST"),
                    ("Marka",    h => h.Contains("MARKA")),
                    ("Kutu",     h => h.Contains("KUTU")),
                    ("AnaMalTL", h => h.Contains("ANA MAL")),
                    ("MFTL",     h => h.Contains("MF")),
                    ("Kurum",    h => h.Contains("KURUM")),
                });
                var missingCols = new[] { "Ay", "Brick", "Marka", "Kutu" }.Where(f => !colMap.ContainsKey(f)).ToList();
                if (missingCols.Any())
                {
                    result.Error = $"Dosya 'Sipariş' şablonuna uymuyor — zorunlu kolon başlıkları bulunamadı: " +
                                   $"{string.Join(", ", missingCols)}. Beklenen: Ay · Brick · Marka · Toplam KUTU " +
                                   "(Personel/TL/Kurum opsiyonel, sıra/ek kolon serbest).";
                    return result;
                }
                int cAy = colMap["Ay"], cBrick = colMap["Brick"], cMarka = colMap["Marka"], cKutu = colMap["Kutu"];
                colMap.TryGetValue("Depot", out int cDepot);
                colMap.TryGetValue("Personel", out int cPersonel);
                colMap.TryGetValue("AnaMalTL", out int cTl);
                colMap.TryGetValue("MFTL", out int cMf);
                colMap.TryGetValue("Kurum", out int cKurum);

                // Ürün eşleşmesi: Name + (çoklu) ImportAlias — hedef importuyla aynı mantık.
                var productList = await db.Products
                    .Where(p => p.IsActive)
                    .Select(p => new { p.ProductId, p.Name, p.ImportAlias })
                    .ToListAsync();
                var productMap = BuildProductMap(productList.Select(p => (p.ProductId, p.Name, p.ImportAlias)));

                var brickMap = await db.Bricks
                    .Where(b => b.IsActive)
                    .ToDictionaryAsync(b => Normalize(b.Name), b => b.BrickId);
                await MergeNameAliasesAsync(db, brickMap, "Brick");

                var userMap = await db.Users
                    .Where(u => u.IsActive && !u.IsDeleted)
                    .ToDictionaryAsync(u => Normalize(u.FullName), u => u.UserId);
                await MergeNameAliasesAsync(db, userMap, "User");

                var periodMap = await db.Periods
                    .ToDictionaryAsync(p => $"{p.Year}-{p.Month:D2}", p => p.PeriodId);

                // Dosyadaki dönemleri bir kez tara (C2 formatı: "2024-01" YYYY-MM)
                var filePeriodIds = new HashSet<int>();
                for (int r = 3; r <= rowCount; r++)
                {
                    var ayStr = ws.Cells[r, cAy].Text?.Trim();
                    if (!string.IsNullOrEmpty(ayStr) && ayStr.Contains('-'))
                    {
                        var parts = ayStr.Split('-');
                        if (int.TryParse(parts[0], out int y) && int.TryParse(parts[1], out int m))
                        {
                            var pk = $"{y}-{m:D2}";
                            if (periodMap.TryGetValue(pk, out var pid)) filePeriodIds.Add(pid);
                        }
                    }
                }

                var existingOrders = replaceExisting
                    ? new List<Order>()
                    : await db.Orders.Where(o => filePeriodIds.Contains(o.PeriodId)).ToListAsync();
                var existingMap = existingOrders
                    .GroupBy(o => $"{o.BrickId}_{o.ProductId}_{o.PeriodId}")
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.ImportedAt).First());

                var toInsert = new List<Order>();
                var errors = new List<string>();
                var unmatchedBricks = new HashSet<string>();
                var unmatchedProducts = new HashSet<string>();
                var unmatchedPeriods = new HashSet<string>();
                int skipped = 0, updated = 0;
                decimal fileTotal = 0m;

                for (int row = 3; row <= rowCount; row++)
                {
                    try
                    {
                        // Gerçek InfiniLens dosya yapısı:
                        // C1=Yıl(int) C2=Ay(YYYY-MM) C3=AnaDepo C4=Brick C5=Personel
                        // C6=Marka C7=ToplamKutu C8=AnaMalTL C9=MFTL C10=KurumAdedi (GLN kolonu yok)
                        var monthStr  = ws.Cells[row, cAy].Text?.Trim();
                        var depot     = cDepot    > 0 ? ws.Cells[row, cDepot].Text?.Trim()  : null;
                        var brickName = ws.Cells[row, cBrick].Text.NormKey();
                        var personel  = cPersonel > 0 ? ws.Cells[row, cPersonel].Text.NormKey() : "";
                        var marka     = ws.Cells[row, cMarka].Text.NormKey();

                        if (string.IsNullOrEmpty(brickName) || brickName == "-") { skipped++; continue; }
                        if (string.IsNullOrEmpty(marka)) { skipped++; continue; }

                        int year = 0, month = 0;
                        if (!string.IsNullOrEmpty(monthStr) && monthStr.Contains('-'))
                        {
                            var parts = monthStr.Split('-');
                            int.TryParse(parts[0], out year);
                            int.TryParse(parts[1], out month);
                        }
                        if (year == 0 || month == 0) { skipped++; continue; }

                        var periodKey = $"{year}-{month:D2}";
                        if (!periodMap.ContainsKey(periodKey)) { unmatchedPeriods.Add(periodKey); skipped++; continue; }
                        if (!brickMap.ContainsKey(brickName)) { unmatchedBricks.Add(brickName!); skipped++; continue; }
                        if (!productMap.ContainsKey(marka)) { unmatchedProducts.Add(marka!); skipped++; continue; }

                        decimal kutu = ReadNum(ws, row, cKutu) ?? 0m;
                        decimal tl   = cTl    > 0 ? (ReadNum(ws, row, cTl) ?? 0m) : 0m;
                        decimal mf   = cMf    > 0 ? (ReadNum(ws, row, cMf) ?? 0m) : 0m;
                        int kurumAdedi = cKurum > 0 ? (int)Math.Round(ReadNum(ws, row, cKurum) ?? 0m) : 0;

                        if (kutu < 0 || tl < 0 || mf < 0 || kurumAdedi < 0)
                        { errors.Add($"Satır {row}: negatif değer"); skipped++; continue; }

                        int? stUserId = null;
                        if (!string.IsNullOrEmpty(personel) && userMap.ContainsKey(personel))
                            stUserId = userMap[personel];

                        var brickId = brickMap[brickName];
                        var productId = productMap[marka];
                        var periodId = periodMap[periodKey];
                        var key = $"{brickId}_{productId}_{periodId}";

                        if (existingMap.TryGetValue(key, out var existing))
                        {
                            existing.Depot = depot;
                            existing.InstitutionCount = kurumAdedi;
                            existing.BoxQuantity = kutu;
                            existing.AmountTL = tl;
                            existing.MFTL = mf;
                            existing.UserId = stUserId;
                            existing.SnapshotDate = batchTime;
                            existing.ImportedAt = batchTime;
                            updated++;
                        }
                        else
                        {
                            var order = new Order
                            {
                                BrickId = brickId,
                                ProductId = productId,
                                PeriodId = periodId,
                                Depot = depot,
                                InstitutionCount = kurumAdedi,
                                BoxQuantity = kutu,
                                AmountTL = tl,
                                MFTL = mf,
                                UserId = stUserId,
                                SnapshotDate = batchTime,
                                ImportedAt = batchTime
                            };
                            toInsert.Add(order);
                            existingMap[key] = order;
                        }
                        fileTotal += kutu;
                    }
                    catch (Exception ex) { errors.Add($"Satır {row}: {ex.Message}"); skipped++; }
                }

                AppendUnmatched(errors, "Brick", unmatchedBricks);
                AppendUnmatched(errors, "Ürün", unmatchedProducts);
                AppendUnmatched(errors, "Dönem", unmatchedPeriods);
                if (dryRun)
                {
                    AppendSuggestions(result.MatchSuggestions, "Brick", unmatchedBricks, brickMap);
                    AppendSuggestions(result.MatchSuggestions, "Product", unmatchedProducts, productMap);
                }

                sw.Stop();
                result.Success = true;
                result.TotalRows = rowCount - 2;
                result.InsertedRows = toInsert.Count;
                result.UpdatedRows = updated;
                result.SkippedRows = skipped;
                result.Errors = errors;
                result.ProcessingMs = (int)sw.ElapsedMilliseconds;
                result.FileTotal = fileTotal;
                result.ExpectedCount = toInsert.Count + updated;

                if (dryRun) return result;

                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    if (replaceExisting && filePeriodIds.Any())
                        await db.Orders.Where(o => filePeriodIds.Contains(o.PeriodId)).ExecuteDeleteAsync();

                    if (toInsert.Any()) db.Orders.AddRange(toInsert);

                    db.ImportLogs.Add(new ImportLog
                    {
                        UserId = userId,
                        FileType = "Sipariş",
                        FileName = fileName,
                        FileHash = fileHash,
                        ImportRowCount = toInsert.Count + updated,
                        ReplacedCount = updated,
                        Status = ClassifyStatus(skipped, errors.Count),
                        ValidationErrors = SerializeErrors(errors),
                        ProcessingTimeMs = result.ProcessingMs,
                        ImportedAt = batchTime
                    });
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }

                result.VerifiedCount = await db.Orders.CountAsync(o => o.ImportedAt == batchTime);
                result.DbTotal = await db.Orders.Where(o => o.ImportedAt == batchTime)
                    .SumAsync(o => (decimal?)o.BoxQuantity) ?? 0m;
                result.Verified = result.VerifiedCount == result.ExpectedCount
                    && AmountsVerified(result.DbTotal, result.FileTotal, result.ExpectedCount);

                await _audit.LogAsync(userId, "Import", "Order", null,
                    newValue: $"{fileName} — {result.InsertedRows}i/{result.UpdatedRows}g ({result.SkippedRows}atl)");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
                if (!dryRun) await TryLogFailureAsync(userId, "Sipariş", fileName, ex.Message, batchTime);
            }

            return result;
        }

        // ============================================================
        // HEDEF IMPORT
        // Satır 1: header, Satır 2+: data
        // Sütunlar: Ay | Mümessil | Brick | Ürün Grubu | Ürün | Hedef Adet
        // ============================================================
        public async Task<ImportResult> ImportTargetsAsync(Stream fileStream, string fileName, int userId, int year = 0, bool replaceExisting = false, bool dryRun = false)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var result = new ImportResult { FileName = fileName, FileType = "Hedef", IsDryRun = dryRun };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var batchTime = DateTime.UtcNow;

            try
            {
                var (bytes, fileHash) = await ReadAllAsync(fileStream);
                result.FileHash = fileHash;
                result.DuplicateOf = await FindPreviousImportAsync(db, "Hedef", fileHash);

                using var package = new ExcelPackage(new MemoryStream(bytes));
                var ws = package.Workbook.Worksheets[0];
                var rowCount = ws.Dimension?.Rows ?? 0;
                if (rowCount < 2) { result.Error = "Dosya boş"; return result; }

                // Kolonları ADINA göre eşle (başlık satırı 1) — fazladan/sırası değişmiş kolonlar tolere edilir.
                var colMap = BuildColumnMap(ws, 1, new (string, Func<string, bool>)[]
                {
                    ("Ay",        h => h == "AY"),
                    ("Mumessil",  h => h.Contains("MÜMESS") || h.Contains("MUMESS") || h.Contains("PERSONEL") || h == "ST"),
                    ("Brick",     h => h.Contains("BRICK")),
                    ("UrunGrubu", h => h.Contains("GRUB")),
                    ("Urun",      h => h.Contains("ÜRÜN") && !h.Contains("GRUB")),
                    ("Hedef",     h => h.Contains("HEDEF")),
                });
                var missingCols = new[] { "Ay", "Mumessil", "Brick", "Urun", "Hedef" }.Where(f => !colMap.ContainsKey(f)).ToList();
                if (missingCols.Any())
                {
                    result.Error = $"Dosya 'Hedef' şablonuna uymuyor — zorunlu kolon başlıkları bulunamadı: " +
                                   $"{string.Join(", ", missingCols)}. Beklenen (satır 1): Ay · Mümessil · Brick · Ürün · HedefAdet " +
                                   "(ÜrünGrubu opsiyonel, sıra/ek kolon serbest).";
                    return result;
                }
                int cAy = colMap["Ay"], cMumessil = colMap["Mumessil"], cBrick = colMap["Brick"],
                    cUrun = colMap["Urun"], cHedef = colMap["Hedef"];
                colMap.TryGetValue("UrunGrubu", out int cGrubu);

                var userMap = await db.Users
                    .Where(u => u.IsActive && !u.IsDeleted && u.RoleId == RoleId.ST)
                    .ToDictionaryAsync(u => Normalize(u.FullName), u => u.UserId);
                await MergeNameAliasesAsync(db, userMap, "User");

                var brickMap = await db.Bricks
                    .Where(b => b.IsActive)
                    .ToDictionaryAsync(b => Normalize(b.Name), b => b.BrickId);
                await MergeNameAliasesAsync(db, brickMap, "Brick");

                // Ürün eşleşmesi: ad VE (çoklu) import takma adı. Ad öncelikli; alias çakışırsa
                // atlanır. Örn. 'DİĞER İTRİYAT' = JOHNSON'S BABY;NEUTROGENA;CAREFREE.
                var productList = await db.Products
                    .Where(p => p.IsActive)
                    .Select(p => new { p.ProductId, p.Name, p.ImportAlias })
                    .ToListAsync();
                var productMap = BuildProductMap(productList.Select(p => (p.ProductId, p.Name, p.ImportAlias)));

                var periodMap = await db.Periods
                    .ToDictionaryAsync(p => $"{p.Year}-{p.Month:D2}", p => p.PeriodId);

                if (year <= 0) year = DateTime.UtcNow.Year;

                // Seçilen yılın dönem ID'leri — silme ve dedup yüklemesi için
                var yearPeriodIds = periodMap
                    .Where(p => p.Key.StartsWith($"{year}-"))
                    .Select(p => p.Value).ToList();

                var existingList = replaceExisting
                    ? new List<Target>()
                    : await db.Targets.Where(t => yearPeriodIds.Contains(t.PeriodId)).ToListAsync();
                var existingMap = existingList
                    .GroupBy(t => $"{t.UserId}_{t.BrickId}_{t.ProductId}_{t.PeriodId}")
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.ImportedAt).First());

                var toInsert = new List<Target>();
                var errors = new List<string>();
                // Atlanan eşleşmeyen değerler — 23k satırlık dosyada satır-satır hata yerine
                // (SerializeErrors 100 ile sınırlı) benzersiz değer + sayı olarak özetlenir.
                var unmatchedUsers = new HashSet<string>();
                var unmatchedBricks = new HashSet<string>();
                var unmatchedProducts = new HashSet<string>();
                var unmatchedPeriods = new HashSet<string>();
                // Aynı dosyada tekrar eden (kişi+brick+ürün+ay) satırları TOPLANIR
                // (alt kırılımlar tek hedefe akar); önceki import'tan gelen kayıt ise
                // dosyadaki ilk satırla değiştirilir.
                var touchedThisBatch = new HashSet<string>();
                int skipped = 0, updated = 0;
                decimal fileTotal = 0m;
                // C: atanmamış satırlar (— / - / (BRICK ATANMAMIŞ)) — şeffaflık için ayrı sayılır.
                int unassignedRows = 0; decimal unassignedQty = 0m;
                // A2: dosyadan türetilen canlı brick→ST haritası (UserBricks senkronu için).
                var brickToUser = new Dictionary<int, int>();

                for (int row = 2; row <= rowCount; row++)
                {
                    try
                    {
                        var ayStr = ws.Cells[row, cAy].Text?.Trim();
                        var mumessil = ws.Cells[row, cMumessil].Text.NormKey();
                        var brickName = ws.Cells[row, cBrick].Text.NormKey();
                        var urunGrubu = cGrubu > 0 ? ws.Cells[row, cGrubu].Text?.Trim() : null;
                        var urunName = ws.Cells[row, cUrun].Text.NormKey();
                        var hedefStr = ws.Cells[row, cHedef].Text?.Trim();

                        if (string.IsNullOrEmpty(ayStr) || string.IsNullOrEmpty(mumessil))
                        { skipped++; continue; }

                        if (!int.TryParse(ayStr, out int ay) || ay < 1 || ay > 12)
                        { errors.Add($"Satır {row}: Geçersiz ay '{ayStr}'"); skipped++; continue; }

                        // Atanmamış satır (— / - / (BRICK ATANMAMIŞ)) — bilinçli; hiçbir ST'ye
                        // düşmez. Toplamı ayrı raporlanır, unmatched gürültüsüne karışmaz.
                        bool mumUnassigned = mumessil == "—" || mumessil == "-";
                        bool brickUnassigned = brickName == "-" || brickName == "(BRICK ATANMAMIŞ)" || string.IsNullOrEmpty(brickName);
                        if (mumUnassigned || brickUnassigned)
                        {
                            decimal uh = ReadNum(ws, row, cHedef) ?? 0m;
                            if (uh > 0) unassignedQty += uh;
                            unassignedRows++; skipped++; continue;
                        }

                        if (!userMap.ContainsKey(mumessil!))
                        { unmatchedUsers.Add(mumessil!); skipped++; continue; }

                        if (!brickMap.ContainsKey(brickName!))
                        { unmatchedBricks.Add(brickName!); skipped++; continue; }

                        if (!productMap.ContainsKey(urunName!))
                        { unmatchedProducts.Add(urunName!); skipped++; continue; }

                        var periodKey = $"{year}-{ay:D2}";
                        if (!periodMap.ContainsKey(periodKey))
                        { unmatchedPeriods.Add(periodKey); skipped++; continue; }

                        var hedefVal = ReadNum(ws, row, cHedef);
                        if (hedefVal == null && !string.IsNullOrEmpty(hedefStr))
                        { errors.Add($"Satır {row}: hedef sayısal okunamadı '{hedefStr}'"); skipped++; continue; }
                        decimal hedef = hedefVal ?? 0m;

                        if (hedef < 0)
                        { errors.Add($"Satır {row}: negatif hedef ({hedef})"); skipped++; continue; }

                        var stUserId = userMap[mumessil!];
                        var brickId = brickMap[brickName!];
                        var productId = productMap[urunName!];
                        var periodId = periodMap[periodKey];
                        var key = $"{stUserId}_{brickId}_{productId}_{periodId}";

                        // A2: bu brick bu ST'ye ait (dosya gerçeği). Dosya içinde tutarlı
                        // (her brick tek ST) varsayılır; son görülen kazanır.
                        brickToUser[brickId] = stUserId;

                        if (existingMap.TryGetValue(key, out var existing))
                        {
                            if (touchedThisBatch.Contains(key))
                            {
                                // Bu dosyada aynı anahtar daha önce işlendi → değerler toplanır
                                existing.TargetQty += hedef;
                            }
                            else
                            {
                                // Önceki import'tan kalan kayıt → dosyadaki değerle değiştir
                                existing.TargetQty = hedef;
                                updated++;
                            }
                            existing.ImportedAt = batchTime;
                        }
                        else
                        {
                            var t = new Target
                            {
                                UserId = stUserId,
                                BrickId = brickId,
                                ProductId = productId,
                                PeriodId = periodId,
                                TargetQty = hedef,
                                ImportedAt = batchTime
                            };
                            toInsert.Add(t);
                            existingMap[key] = t;
                        }
                        touchedThisBatch.Add(key);
                        fileTotal += hedef;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Satır {row}: {ex.Message}");
                        skipped++;
                    }
                }

                AppendUnmatched(errors, "Mümessil (pasif/silinmiş ST veya isim uyuşmazlığı)", unmatchedUsers);
                AppendUnmatched(errors, "Brick", unmatchedBricks);
                AppendUnmatched(errors, "Ürün", unmatchedProducts);
                AppendUnmatched(errors, "Dönem (Periods'ta tanımsız ay — /periods'tan ekleyin)", unmatchedPeriods);
                if (dryRun)
                {
                    AppendSuggestions(result.MatchSuggestions, "User", unmatchedUsers, userMap);
                    AppendSuggestions(result.MatchSuggestions, "Brick", unmatchedBricks, brickMap);
                    AppendSuggestions(result.MatchSuggestions, "Product", unmatchedProducts, productMap);
                }

                sw.Stop();
                result.Success = true;
                result.TotalRows = rowCount - 1;
                result.InsertedRows = toInsert.Count;
                result.UpdatedRows = updated;
                result.SkippedRows = skipped;
                result.Errors = errors;
                result.ProcessingMs = (int)sw.ElapsedMilliseconds;
                result.FileTotal = fileTotal;
                result.ExpectedCount = toInsert.Count + updated;
                result.UnassignedTargetRows = unassignedRows;
                result.UnassignedTargetQty = unassignedQty;

                // A2: brick→ST haritası önizlemesi (uygulanmadan sayılır) — dry-run'da da görünür.
                var (syncPreview, deactPreview) = await SyncBrickAssignmentsAsync(db, brickToUser, batchTime, apply: false);
                result.BrickAssignmentsSynced = syncPreview;
                result.BrickAssignmentsDeactivated = deactPreview;

                if (dryRun) return result;

                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    if (replaceExisting && yearPeriodIds.Any())
                        await db.Targets.Where(t => yearPeriodIds.Contains(t.PeriodId)).ExecuteDeleteAsync();

                    if (toInsert.Any()) db.Targets.AddRange(toInsert);

                    // A2: brick→ST haritasını UserBricks'e uygula (aynı transaction — atomik).
                    await SyncBrickAssignmentsAsync(db, brickToUser, batchTime, apply: true);

                    db.ImportLogs.Add(new ImportLog
                    {
                        UserId = userId,
                        FileType = "Hedef",
                        FileName = fileName,
                        FileHash = fileHash,
                        ImportRowCount = toInsert.Count + updated,
                        ReplacedCount = updated,
                        Status = ClassifyStatus(skipped, errors.Count),
                        ValidationErrors = SerializeErrors(errors),
                        ProcessingTimeMs = result.ProcessingMs,
                        ImportedAt = batchTime
                    });
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }

                result.VerifiedCount = await db.Targets.CountAsync(t => t.ImportedAt == batchTime);
                result.DbTotal = await db.Targets.Where(t => t.ImportedAt == batchTime)
                    .SumAsync(t => (decimal?)t.TargetQty) ?? 0m;
                result.Verified = result.VerifiedCount == result.ExpectedCount
                    && AmountsVerified(result.DbTotal, result.FileTotal, result.ExpectedCount);

                await _audit.LogAsync(userId, "Import", "Target", null,
                    newValue: $"{fileName} — {result.InsertedRows}i/{result.UpdatedRows}g ({result.SkippedRows}atl)");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
                if (!dryRun) await TryLogFailureAsync(userId, "Hedef", fileName, ex.Message, batchTime);
            }

            return result;
        }

        // ============================================================
        // BRICK → ST ATAMA IMPORT (N_St_Brick.xlsx)
        // Satır 1: header, Satır 2+: data
        // Sütunlar: Brick | Mümessil
        // Tek otoriter brick→ST haritası: replaceExisting=true ise dosyada olmayan tüm
        // aktif atamalar pasiflenir, dosyadaki harita UserBricks'e yazılır. Hedef ve sell-out
        // bu tek haritadan atfedildiği için "Hedef vs Gerçekleşen" çakışması kalıcı olarak çözülür.
        // ============================================================
        public async Task<ImportResult> ImportBrickAssignmentsAsync(Stream fileStream, string fileName, int userId, bool replaceExisting = false, bool dryRun = false)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var result = new ImportResult { FileName = fileName, FileType = "BrickAtama", IsDryRun = dryRun };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var batchTime = DateTime.UtcNow;

            try
            {
                var (bytes, fileHash) = await ReadAllAsync(fileStream);
                result.FileHash = fileHash;
                result.DuplicateOf = await FindPreviousImportAsync(db, "BrickAtama", fileHash);

                using var package = new ExcelPackage(new MemoryStream(bytes));
                var ws = package.Workbook.Worksheets[0];
                var rowCount = ws.Dimension?.Rows ?? 0;
                if (rowCount < 2) { result.Error = "Dosya boş"; return result; }

                var headerErr = ValidateHeader(ws, 1, new[] { (1, "Brick"), (2, "Mümessil") }, "Brick–ST Ataması");
                if (headerErr != null) { result.Error = headerErr; return result; }

                var userMap = await db.Users
                    .Where(u => u.IsActive && !u.IsDeleted && u.RoleId == RoleId.ST)
                    .ToDictionaryAsync(u => Normalize(u.FullName), u => u.UserId);
                await MergeNameAliasesAsync(db, userMap, "User");
                var brickMap = await db.Bricks
                    .Where(b => b.IsActive)
                    .ToDictionaryAsync(b => Normalize(b.Name), b => b.BrickId);
                await MergeNameAliasesAsync(db, brickMap, "Brick");

                var brickToUser = new Dictionary<int, int>();
                var errors = new List<string>();
                var unmatchedUsers = new HashSet<string>();
                var unmatchedBricks = new HashSet<string>();
                int skipped = 0;
                var fileBricks = new HashSet<string>();

                for (int row = 2; row <= rowCount; row++)
                {
                    var brickName = ws.Cells[row, 1].Text.NormKey();
                    var mumessil = ws.Cells[row, 2].Text.NormKey();
                    if (string.IsNullOrEmpty(brickName)) { skipped++; continue; }
                    fileBricks.Add(brickName!);

                    // Atanmamış brick satırları (— / - / (BRICK ATANMAMIŞ)) bilinçli atlanır.
                    if (string.IsNullOrEmpty(mumessil) || mumessil == "—" || mumessil == "-"
                        || brickName == "-" || brickName == "(BRICK ATANMAMIŞ)")
                    { skipped++; continue; }

                    if (!userMap.TryGetValue(mumessil!, out var stUserId))
                    { unmatchedUsers.Add(mumessil!); skipped++; continue; }
                    if (!brickMap.TryGetValue(brickName!, out var brickId))
                    { unmatchedBricks.Add(brickName!); skipped++; continue; }

                    brickToUser[brickId] = stUserId; // son görülen kazanır (dosya tutarlı varsayılır)
                }

                AppendUnmatched(errors, "Mümessil (pasif/silinmiş ST veya isim uyuşmazlığı)", unmatchedUsers);
                AppendUnmatched(errors, "Brick", unmatchedBricks);
                if (dryRun)
                {
                    AppendSuggestions(result.MatchSuggestions, "User", unmatchedUsers, userMap);
                    AppendSuggestions(result.MatchSuggestions, "Brick", unmatchedBricks, brickMap);
                }

                result.TotalRows = rowCount - 1;
                result.SkippedRows = skipped;
                result.TotalBrickCount = fileBricks.Count;
                result.MatchedBrickCount = brickToUser.Count;
                result.Errors = errors;

                // replace modunda dosyada olmayan aktif atamaları say (önizleme + uygulama).
                var extraToDeactivate = replaceExisting
                    ? await db.UserBricks
                        .Where(ub => ub.IsActive && !brickToUser.Keys.Contains(ub.BrickId))
                        .ToListAsync()
                    : new List<UserBrick>();

                var (syncPreview, deactPreview) = await SyncBrickAssignmentsAsync(db, brickToUser, batchTime, apply: false);
                result.BrickAssignmentsSynced = syncPreview;
                result.BrickAssignmentsDeactivated = deactPreview + extraToDeactivate.Count;
                result.ExpectedCount = brickToUser.Count;

                sw.Stop();
                result.Success = true;
                result.ProcessingMs = (int)sw.ElapsedMilliseconds;

                if (dryRun) return result;

                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    if (replaceExisting)
                        foreach (var ub in extraToDeactivate) { ub.IsActive = false; ub.UnassignedAt = batchTime; }

                    await SyncBrickAssignmentsAsync(db, brickToUser, batchTime, apply: true);

                    db.ImportLogs.Add(new ImportLog
                    {
                        UserId = userId,
                        FileType = "BrickAtama",
                        FileName = fileName,
                        FileHash = fileHash,
                        ImportRowCount = brickToUser.Count,
                        ReplacedCount = result.BrickAssignmentsDeactivated,
                        Status = ClassifyStatus(skipped, errors.Count),
                        ValidationErrors = SerializeErrors(errors),
                        ProcessingTimeMs = result.ProcessingMs,
                        ImportedAt = batchTime
                    });
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }

                result.VerifiedCount = await db.UserBricks.CountAsync(ub => ub.IsActive);
                result.Verified = errors.Count == 0;

                await _audit.LogAsync(userId, "Import", "UserBrick", null,
                    newValue: $"{fileName} — {result.BrickAssignmentsSynced} atama, {result.BrickAssignmentsDeactivated} pasif");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
                if (!dryRun) await TryLogFailureAsync(userId, "BrickAtama", fileName, ex.Message, batchTime);
            }

            return result;
        }

        // ============================================================
        // RUTA UYUM IMPORT
        // Satır 1: başlık, Satır 2+: veri
        // Sütunlar: Personel | Planlanan Ziyaret | Gerçekleşen Ziyaret
        // ============================================================
        public async Task<ImportResult> ImportRutaAsync(Stream fileStream, string fileName, int userId, int year, string quarter, bool replaceExisting = false, bool dryRun = false)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var result = new ImportResult { FileName = fileName, FileType = "Ruta", IsDryRun = dryRun };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var batchTime = DateTime.UtcNow;
            try
            {
                var (bytes, fileHash) = await ReadAllAsync(fileStream);
                result.FileHash = fileHash;
                result.DuplicateOf = await FindPreviousImportAsync(db, "Ruta", fileHash);

                using var package = new ExcelPackage(new MemoryStream(bytes));
                var ws = package.Workbook.Worksheets[0];
                var rowCount = ws.Dimension?.Rows ?? 0;
                if (rowCount < 2) { result.Error = "Dosya boş"; return result; }

                var headerErr = ValidateHeader(ws, 1, new[] { (1, "Personel"), (2, "Plan"), (3, "Ger") }, "Ruta Uyum");
                if (headerErr != null) { result.Error = headerErr; return result; }

                var userMap = await db.Users
                    .Where(u => u.IsActive && !u.IsDeleted && u.RoleId == RoleId.ST)
                    .ToDictionaryAsync(u => Normalize(u.FullName), u => u.UserId);
                await MergeNameAliasesAsync(db, userMap, "User");

                var existingMap = replaceExisting
                    ? new Dictionary<int, RutaUyum>()
                    : await db.RutaUyum
                        .Where(r => r.Year == year && r.Quarter == quarter)
                        .ToDictionaryAsync(r => r.UserId);

                var toInsert = new List<RutaUyum>();
                var errors = new List<string>();
                int skipped = 0, updated = 0;
                decimal fileTotal = 0m; // gerçekleşen ziyaret toplamı (teyit için)

                for (int row = 2; row <= rowCount; row++)
                {
                    try
                    {
                        var personel    = ws.Cells[row, 1].Text.NormKey();

                        if (string.IsNullOrEmpty(personel)) { skipped++; continue; }
                        if (!userMap.TryGetValue(personel, out var stUserId))
                        { errors.Add($"Satır {row}: Personel bulunamadı '{personel}'"); skipped++; continue; }

                        int planned = (int)Math.Round(ReadNum(ws, row, 2) ?? 0m);
                        int actual  = (int)Math.Round(ReadNum(ws, row, 3) ?? 0m);

                        if (planned < 0 || actual < 0)
                        { errors.Add($"Satır {row}: negatif ziyaret sayısı"); skipped++; continue; }

                        decimal rate = planned > 0 ? Math.Min((decimal)actual / planned, 1.5m) : 0m;

                        if (existingMap.TryGetValue(stUserId, out var existing))
                        {
                            existing.PlannedVisits = planned;
                            existing.ActualVisits  = actual;
                            existing.Rate          = rate;
                            existing.ImportedAt    = batchTime;
                            updated++;
                        }
                        else
                        {
                            var r = new RutaUyum
                            {
                                UserId = stUserId, Year = year, Quarter = quarter,
                                PlannedVisits = planned, ActualVisits = actual, Rate = rate,
                                ImportedAt = batchTime
                            };
                            toInsert.Add(r);
                            existingMap[stUserId] = r;
                        }
                        fileTotal += actual;
                    }
                    catch (Exception ex) { errors.Add($"Satır {row}: {ex.Message}"); skipped++; }
                }

                sw.Stop();
                result.Success = true;
                result.TotalRows = rowCount - 1;
                result.InsertedRows = toInsert.Count;
                result.UpdatedRows = updated;
                result.SkippedRows = skipped;
                result.Errors = errors;
                result.ProcessingMs = (int)sw.ElapsedMilliseconds;
                result.FileTotal = fileTotal;
                result.ExpectedCount = toInsert.Count + updated;

                if (dryRun) return result;

                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    if (replaceExisting)
                        await db.RutaUyum.Where(r => r.Year == year && r.Quarter == quarter
                            && r.ImportedAt != batchTime).ExecuteDeleteAsync();

                    if (toInsert.Any()) db.RutaUyum.AddRange(toInsert);

                    db.ImportLogs.Add(new ImportLog
                    {
                        UserId = userId, FileType = "Ruta", FileName = fileName, FileHash = fileHash,
                        ImportRowCount = toInsert.Count + updated, ReplacedCount = updated,
                        Status = ClassifyStatus(skipped, errors.Count),
                        ValidationErrors = SerializeErrors(errors),
                        ProcessingTimeMs = result.ProcessingMs,
                        ImportedAt = batchTime
                    });
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }

                result.VerifiedCount = await db.RutaUyum.CountAsync(r => r.ImportedAt == batchTime);
                result.DbTotal = await db.RutaUyum.Where(r => r.ImportedAt == batchTime)
                    .SumAsync(r => (decimal?)r.ActualVisits) ?? 0m;
                result.Verified = result.VerifiedCount == result.ExpectedCount
                    && AmountsVerified(result.DbTotal, result.FileTotal, result.ExpectedCount);

                await _audit.LogAsync(userId, "Import", "Ruta", null,
                    newValue: $"{fileName} — {result.InsertedRows}i/{result.UpdatedRows}g ({result.SkippedRows}atl)");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
                if (!dryRun) await TryLogFailureAsync(userId, "Ruta", fileName, ex.Message, batchTime);
            }
            return result;
        }

        // ============================================================
        // ZİYARET PLANI IMPORT
        // Sütunlar: Bölge | Kadro/ÜTT-ST | Tarih | İl | Brick | GLN | ...
        // ============================================================
        public async Task<ImportResult> ImportVisitPlanAsync(Stream fileStream, string fileName, int userId, int year, string quarter, bool dryRun = false)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var result = new ImportResult { FileName = fileName, FileType = "ZiyaretPlan", IsDryRun = dryRun };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var batchTime = DateTime.UtcNow;
            try
            {
                var (bytes, fileHash) = await ReadAllAsync(fileStream);
                result.FileHash = fileHash;
                result.DuplicateOf = await FindPreviousImportAsync(db, "ZiyaretPlan", fileHash);

                using var package = new ExcelPackage(new MemoryStream(bytes));
                var ws = package.Workbook.Worksheets[0];
                var rowCount = ws.Dimension?.Rows ?? 0;
                if (rowCount < 2) { result.Error = "Dosya boş"; return result; }

                var headerErr = ValidateHeader(ws, 1, new[] { (6, "Brick"), (7, "GLN") }, "Ziyaret Planı");
                if (headerErr != null) { result.Error = headerErr; return result; }

                var brickMap = await db.Bricks
                    .Where(b => b.IsActive)
                    .ToDictionaryAsync(b => Normalize(b.Name), b => b.BrickId);
                await MergeNameAliasesAsync(db, brickMap, "Brick");

                var userBrickMap = (await db.UserBricks
                    .Where(ub => ub.IsActive)
                    .ToListAsync())
                    .GroupBy(ub => ub.BrickId)
                    .ToDictionary(g => g.Key, g => g.First().UserId);

                // Distinct GLN+UserId seti (aynı GLN birden fazla satırda gelebilir)
                var seen = new HashSet<string>();
                var toInsert = new List<VisitPlan>();
                var errors = new List<string>();
                int skipped = 0;

                // Satır 1 başlık — satır 2'den itibaren veri
                for (int row = 2; row <= rowCount; row++)
                {
                    try
                    {
                        // C1=Bölge C2=Kadro C3=ÜTT C4=Tarih C5=İl C6=Brick C7=GLN
                        var brickName = ws.Cells[row, 6].Text.NormKey();
                        var gln = ws.Cells[row, 7].Text?.Trim();

                        if (string.IsNullOrEmpty(gln)) { skipped++; continue; }
                        if (string.IsNullOrEmpty(brickName)) { skipped++; continue; }

                        if (!brickMap.TryGetValue(brickName, out var brickId))
                        { errors.Add($"Satır {row}: Brick bulunamadı '{brickName}'"); skipped++; continue; }

                        userBrickMap.TryGetValue(brickId, out var stUserId);

                        var dedupKey = $"{gln}_{stUserId}";
                        if (!seen.Add(dedupKey)) { skipped++; continue; }

                        toInsert.Add(new VisitPlan
                        {
                            GLN = gln,
                            BrickId = brickId,
                            UserId = stUserId == 0 ? null : stUserId,
                            Year = year,
                            Quarter = quarter,
                            ImportedAt = batchTime
                        });
                    }
                    catch (Exception ex) { errors.Add($"Satır {row}: {ex.Message}"); skipped++; }
                }

                sw.Stop();
                result.Success = true;
                result.TotalRows = rowCount - 1;
                result.InsertedRows = toInsert.Count;
                result.SkippedRows = skipped;
                result.Errors = errors;
                result.ProcessingMs = (int)sw.ElapsedMilliseconds;
                result.ExpectedCount = toInsert.Count;

                if (dryRun) return result;

                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    // Aynı year+quarter için mevcut kayıtları sil (tam replace)
                    var replaced = await db.VisitPlans
                        .Where(v => v.Year == year && v.Quarter == quarter).ExecuteDeleteAsync();

                    if (toInsert.Any()) db.VisitPlans.AddRange(toInsert);

                    db.ImportLogs.Add(new ImportLog
                    {
                        UserId = userId, FileType = "ZiyaretPlan", FileName = fileName, FileHash = fileHash,
                        ImportRowCount = toInsert.Count, ReplacedCount = replaced,
                        Status = ClassifyStatus(skipped, errors.Count),
                        ValidationErrors = SerializeErrors(errors),
                        ProcessingTimeMs = result.ProcessingMs,
                        ImportedAt = batchTime
                    });
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }

                // Ziyaret planında miktar kolonu yok — yalnızca satır sayısı teyidi
                result.VerifiedCount = await db.VisitPlans.CountAsync(v => v.ImportedAt == batchTime);
                result.Verified = result.VerifiedCount == result.ExpectedCount;

                await _audit.LogAsync(userId, "Import", "VisitPlan", null,
                    newValue: $"{fileName} — {result.InsertedRows}i ({result.SkippedRows}atl)");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
                if (!dryRun) await TryLogFailureAsync(userId, "ZiyaretPlan", fileName, ex.Message, batchTime);
            }
            return result;
        }

        // ============================================================
        // ECZANE PLANI IMPORT (UTT_Brick raporu — ECZANE sayfası)
        // Sütunlar: Ekip | Bölge | Çalışan ÜTT | Brick | Çalışan ÜTT | Segment | Uzmanlık |
        //           Ünvan | SMM ID | SMM Adı Soyadı | Kurum | GLN | Frek. | Plan | Ziyaret
        //
        // Ziyaret planına ALINMIŞ eczaneleri PharmacyPlans'a yazar. Amaç yalnız işaretleme:
        // analizlerde "planlı" eczaneleri ana kütleden ayırabilmek. Hiçbir KPI'ya girmez —
        // VisitPlans'a (Strike Rate girdisi) bilinçli olarak DOKUNMAZ.
        //
        // NOT: Kaynak dosyada "Çalışan ÜTT" başlığı İKİ KEZ geçer (kolon 3 ve 5) ve değerleri
        // birebir aynıdır (7.419/7.419 satırda özdeş). BuildColumnMap "ilk eşleşen kazanır"
        // kuralıyla kolon 3'ü alır; hangisi alınırsa alınsın sonuç değişmez.
        // ============================================================
        /// <summary>
        /// Eczane planı kolon eşleme kuralları. Ayrı metot: test edilebilsin (InternalsVisibleTo).
        /// Eşleme <see cref="Normalize"/> üzerinden yapılır — büyük/küçük ve Türkçe İ/ı duyarsız,
        /// iç boşluklar tekil. Bu yüzden "Frek.\n" → "FREK." ve "Ziyaret" → "ZIYARET" olur.
        /// "Utt" için <c>Contains("ÜTT")</c> yeterli: Ü katlanmadığından "Uzmanlık"/"Ünvan" eşleşmez.
        /// "Plan"/"SmmId" TAM eşleşme ister — "Planlanan"/"SMM Adı Soyadı" gibi komşuları kapmasın.
        /// </summary>
        internal static (string Field, Func<string, bool> Match)[] PharmacyPlanSpecs() => new (string, Func<string, bool>)[]
        {
            ("Utt",     n => n.Contains("ÜTT")),
            ("Brick",   n => n.Contains("BRICK")),
            ("Gln",     n => n.Contains("GLN")),
            ("Kurum",   n => n == "KURUM"),
            ("Segment", n => n.Contains("SEGMENT")),
            ("Frek",    n => n.Contains("FREK")),
            ("Plan",    n => n == "PLAN"),
            ("Ziyaret", n => n.Contains("ZIYARET")),
            ("SmmId",   n => n == "SMM ID"),
        };

        public async Task<ImportResult> ImportPharmacyPlanAsync(Stream fileStream, string fileName, int userId, int year, bool replaceExisting = false, bool dryRun = false)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var result = new ImportResult { FileName = fileName, FileType = "EczanePlani", IsDryRun = dryRun };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var batchTime = DateTime.UtcNow;
            try
            {
                if (year <= 0) { result.Error = "Yıl seçilmedi"; return result; }

                var (bytes, fileHash) = await ReadAllAsync(fileStream);
                result.FileHash = fileHash;
                result.DuplicateOf = await FindPreviousImportAsync(db, "EczanePlani", fileHash);

                using var package = new ExcelPackage(new MemoryStream(bytes));
                // Dosyada 3 sayfa var: ECZANE (ana), St-Brick-Pharma (türev — ECZANE'nin dar hali,
                // birebir aynı küme), Brick-St (brick→ST; o ayrı "Brick–ST Ataması" paneliyle yüklenir).
                var ws = package.Workbook.Worksheets
                             .FirstOrDefault(s => Normalize(s.Name) == "ECZANE")
                         ?? package.Workbook.Worksheets[0];
                var rowCount = ws.Dimension?.Rows ?? 0;
                if (rowCount < 2) { result.Error = "Dosya boş"; return result; }

                // Kolon eşlemesi ADINA göre — sabit konum yerine (araya kolon girerse bozulmasın).
                var colMap = BuildColumnMap(ws, 1, PharmacyPlanSpecs());

                colMap.TryGetValue("Utt", out int cUtt);
                colMap.TryGetValue("Brick", out int cBrick);
                colMap.TryGetValue("Gln", out int cGln);
                colMap.TryGetValue("Kurum", out int cKurum);
                colMap.TryGetValue("Segment", out int cSegment);
                colMap.TryGetValue("Frek", out int cFrek);
                colMap.TryGetValue("Plan", out int cPlan);
                colMap.TryGetValue("Ziyaret", out int cZiyaret);
                colMap.TryGetValue("SmmId", out int cSmmId);

                // Yanlış panele yanlış dosya kapısı: bu üçü olmadan dosya bu şablon değildir.
                var eksik = new List<string>();
                if (cGln == 0) eksik.Add("GLN");
                if (cBrick == 0) eksik.Add("Brick");
                if (cPlan == 0) eksik.Add("Plan");
                if (eksik.Count > 0)
                {
                    var bulunan = Enumerable.Range(1, ws.Dimension?.Columns ?? 0)
                        .Select(c => ws.Cells[1, c].Text?.Trim())
                        .Where(t => !string.IsNullOrEmpty(t));
                    result.Error = $"Dosya 'Eczane Planı' şablonuna uymuyor — bulunamayan kolon(lar): " +
                                   $"{string.Join(" · ", eksik)}. Dosyadaki başlıklar: {string.Join(" · ", bulunan)}. " +
                                   $"Doğru paneli/dosyayı seçtiğinizden emin olun.";
                    return result;
                }

                var userMap = await db.Users
                    .Where(u => u.RoleId == RoleId.ST && u.IsActive && !u.IsDeleted)
                    .ToDictionaryAsync(u => Normalize(u.FullName), u => u.UserId);
                await MergeNameAliasesAsync(db, userMap, "User");

                var brickMap = await db.Bricks
                    .Where(b => b.IsActive)
                    .ToDictionaryAsync(b => Normalize(b.Name), b => b.BrickId);
                await MergeNameAliasesAsync(db, brickMap, "Brick");

                // Tüm aralığı tek çağrıyla belleğe al (per-hücre EPPlus erişimi ~10-30× yavaş).
                var dim = ws.Dimension!;
                int r0 = dim.Start.Row, c0 = dim.Start.Column;
                var data = (object[,])ws.Cells[dim.Address].Value;
                int dRows = data.GetLength(0), dCols = data.GetLength(1);
                object? CellAt(int row, int col)
                {
                    int rr = row - r0, cc = col - c0;
                    return (rr >= 0 && cc >= 0 && rr < dRows && cc < dCols) ? data[rr, cc] : null;
                }
                string? CellStr(int row, int col) => col > 0 ? CellAt(row, col)?.ToString()?.Trim() : null;
                int CellInt(int row, int col)
                {
                    if (col <= 0) return 0;
                    var v = ParseCellNumber(CellAt(row, col), CellStr(row, col));
                    return v.HasValue ? (int)Math.Round(v.Value) : 0;
                }

                var existingList = replaceExisting
                    ? new List<PharmacyPlan>()
                    : await db.PharmacyPlans.Where(p => p.Year == year).ToListAsync();
                var existingMap = existingList
                    .GroupBy(p => $"{p.GLN}_{p.UserId ?? 0}_{p.BrickId ?? 0}")
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.ImportedAt).First());

                var toInsert = new List<PharmacyPlan>();
                var errors = new List<string>();
                var unmatchedUsers = new HashSet<string>();
                var unmatchedBricks = new HashSet<string>();
                // Aynı dosyada tekrar eden (GLN+ST+brick) satırlar TOPLANIR; önceki import'tan
                // kalan kayıt ise dosyadaki ilk satırla DEĞİŞTİRİLİR (hedef importuyla aynı desen).
                var touchedThisBatch = new HashSet<string>();
                int skipped = 0, updated = 0, blankGln = 0;
                int blankGlnPlan = 0, blankGlnZiyaret = 0;
                decimal fileTotal = 0m;   // işlenen satırların toplam PlannedVisits'i (teyit için)

                for (int row = 2; row <= rowCount; row++)
                {
                    try
                    {
                        var uttName   = Normalize(CellStr(row, cUtt));
                        var brickName = Normalize(CellStr(row, cBrick));
                        // GLN HAM hücre değerinden okunur (.Text değil): dosyada 7.367 GLN sayısal,
                        // biçimlendirme .Text'i bozabilir. Sell-out importu da böyle okuyor.
                        var gln       = CellStr(row, cGln);
                        int plan      = CellInt(row, cPlan);
                        int ziyaret   = CellInt(row, cZiyaret);

                        // Tamamen boş satır (dosya sonu dolgusu) — sessiz geç.
                        if (string.IsNullOrEmpty(gln) && string.IsNullOrEmpty(brickName)
                            && string.IsNullOrEmpty(uttName) && plan == 0) continue;

                        // Boş GLN → eşleşemez. Kaynak dosyada 52 satır böyle; sessizce yutmak
                        // yerine ayrı sayılır ve raporlanır (veri kalitesi sorunu).
                        if (string.IsNullOrEmpty(gln))
                        {
                            blankGln++; blankGlnPlan += plan; blankGlnZiyaret += ziyaret;
                            skipped++; continue;
                        }

                        if (plan < 0 || ziyaret < 0)
                        { errors.Add($"Satır {row}: negatif plan/ziyaret ({plan}/{ziyaret})"); skipped++; continue; }

                        int? uid = null;
                        if (!string.IsNullOrEmpty(uttName))
                        {
                            if (userMap.TryGetValue(uttName!, out var u)) uid = u;
                            else unmatchedUsers.Add(uttName!);
                        }

                        int? bid = null;
                        if (!string.IsNullOrEmpty(brickName))
                        {
                            if (brickMap.TryGetValue(brickName!, out var b)) bid = b;
                            else unmatchedBricks.Add(brickName!);
                        }

                        var key = $"{gln}_{uid ?? 0}_{bid ?? 0}";

                        if (existingMap.TryGetValue(key, out var existing))
                        {
                            if (touchedThisBatch.Contains(key))
                            {
                                existing.PlannedVisits += plan;
                                existing.ActualVisits += ziyaret;
                            }
                            else
                            {
                                existing.PlannedVisits = plan;
                                existing.ActualVisits = ziyaret;
                                updated++;
                            }
                            existing.Frequency = CellInt(row, cFrek);
                            existing.Segment = CellStr(row, cSegment);
                            existing.KurumName = CellStr(row, cKurum);
                            existing.SmmId = CellStr(row, cSmmId);
                            existing.ImportedAt = batchTime;
                        }
                        else
                        {
                            var p = new PharmacyPlan
                            {
                                GLN = gln!,
                                UserId = uid,
                                BrickId = bid,
                                Year = year,
                                Segment = CellStr(row, cSegment),
                                Frequency = CellInt(row, cFrek),
                                PlannedVisits = plan,
                                ActualVisits = ziyaret,
                                KurumName = CellStr(row, cKurum),
                                SmmId = CellStr(row, cSmmId),
                                ImportedAt = batchTime
                            };
                            toInsert.Add(p);
                            existingMap[key] = p;
                        }
                        touchedThisBatch.Add(key);
                        fileTotal += plan;
                    }
                    catch (Exception ex) { errors.Add($"Satır {row}: {ex.Message}"); skipped++; }
                }

                if (blankGln > 0)
                    errors.Add($"GLN'i boş {blankGln} satır atlandı (kaynak raporda düzeltilmeli) — " +
                               $"görünmez olan: {blankGlnPlan} plan / {blankGlnZiyaret} ziyaret");
                AppendUnmatched(errors, "Çalışan ÜTT (DB'de ST kaydı yok)", unmatchedUsers);
                AppendUnmatched(errors, "Brick", unmatchedBricks);

                sw.Stop();
                result.Success = true;
                result.TotalRows = rowCount - 1;
                result.InsertedRows = toInsert.Count;
                result.UpdatedRows = updated;
                result.SkippedRows = skipped;
                result.BlankGlnRows = blankGln;
                result.Errors = errors;
                result.ProcessingMs = (int)sw.ElapsedMilliseconds;
                result.ExpectedCount = toInsert.Count + updated;
                result.FileTotal = fileTotal;

                if (dryRun)
                {
                    AppendSuggestions(result.MatchSuggestions, "User", unmatchedUsers, userMap);
                    AppendSuggestions(result.MatchSuggestions, "Brick", unmatchedBricks, brickMap);
                    return result;
                }

                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    int replaced = 0;
                    if (replaceExisting)
                        replaced = await db.PharmacyPlans.Where(p => p.Year == year).ExecuteDeleteAsync();

                    if (toInsert.Any()) db.PharmacyPlans.AddRange(toInsert);

                    db.ImportLogs.Add(new ImportLog
                    {
                        UserId = userId, FileType = "EczanePlani", FileName = fileName, FileHash = fileHash,
                        ImportRowCount = toInsert.Count + updated, ReplacedCount = replaced,
                        Status = ClassifyStatus(skipped, errors.Count),
                        ValidationErrors = SerializeErrors(errors),
                        ProcessingTimeMs = result.ProcessingMs,
                        ImportedAt = batchTime
                    });
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }

                // Commit sonrası teyit: satır sayısı + plan toplamı
                result.VerifiedCount = await db.PharmacyPlans.CountAsync(p => p.ImportedAt == batchTime);
                result.DbTotal = await db.PharmacyPlans.Where(p => p.ImportedAt == batchTime)
                    .SumAsync(p => (decimal?)p.PlannedVisits) ?? 0m;
                result.Verified = result.VerifiedCount == result.ExpectedCount
                                  && AmountsVerified(result.DbTotal, result.FileTotal, result.ExpectedCount);

                await _audit.LogAsync(userId, "Import", "PharmacyPlan", null,
                    newValue: $"{fileName} — {result.InsertedRows}i/{updated}g ({result.SkippedRows}atl, {blankGln} boş GLN) yıl {year}");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
                if (!dryRun) await TryLogFailureAsync(userId, "EczanePlani", fileName, ex.Message, batchTime);
            }
            return result;
        }

        // ============================================================
        // ÜRÜN-MARKA EŞLEŞTİRME (Barkod-Product master)
        // Satır 1: boş, Satır 2: header, Satır 3+: data
        // Sütunlar: Barkod(1) | Marka(2) | Ürün(3) | Product(4, kanonik ürün adı) | Urun Grubu(5, ITR/OTC)
        // InfiniLens "Marka" → prim "Product" alias'larının TEK doğruluk kaynağı.
        // Her kanonik Product için ona düşen tüm Marka değerleri ImportAlias'a, Urun Grubu da
        // Product.ProductGroup'a yazılır. (Urun Grubu kolonu yoksa grup yazılmaz — geriye uyumlu.)
        // ============================================================
        public async Task<ImportResult> ImportProductMapAsync(Stream fileStream, string fileName, int userId, bool dryRun = false)
        {
            await EnsureAdminAsync(userId);
            using var db = _factory.CreateDbContext();
            var result = new ImportResult { FileName = fileName, FileType = "UrunMarka", IsDryRun = dryRun };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var batchTime = DateTime.UtcNow;

            try
            {
                var (bytes, fileHash) = await ReadAllAsync(fileStream);
                result.FileHash = fileHash;
                result.DuplicateOf = await FindPreviousImportAsync(db, "UrunMarka", fileHash);

                using var package = new ExcelPackage(new MemoryStream(bytes));
                var ws = package.Workbook.Worksheets[0];
                var rowCount = ws.Dimension?.Rows ?? 0;
                if (rowCount < 3) { result.Error = "Dosya boş veya hatalı format"; return result; }

                var headerErr = ValidateHeader(ws, 2, new[] { (2, "Marka"), (3, "Ürün"), (4, "Product") }, "Ürün-Marka Eşleştirme", minMatch: 2);
                if (headerErr != null) { result.Error = headerErr; return result; }

                // DB ürünleri ad ile (kanonik Product = Product.Name eşleşmesi)
                var products = await db.Products.ToListAsync();
                var productByName = products
                    .GroupBy(p => Normalize(p.Name))
                    .ToDictionary(g => g.Key, g => g.First());

                // productId → o ürüne düşen distinct markalar (sırayı koruyan set)
                var aliasByProduct = new Dictionary<int, List<string>>();
                // productId → ürün grubu (col 5, ilk dolu değer; ITR/OTC)
                var groupByProduct = new Dictionary<int, string>();
                var seenBrands = new HashSet<string>();
                var unmatchedCanonical = new HashSet<string>();
                var unmatchedBrands = new HashSet<string>();
                var errors = new List<string>();
                int skipped = 0;

                for (int row = 3; row <= rowCount; row++)
                {
                    try
                    {
                        var marka = ws.Cells[row, 2].Text?.Trim();
                        var canonical = ws.Cells[row, 4].Text?.Trim();
                        var grup = ws.Cells[row, 5].Text?.Trim();

                        if (string.IsNullOrEmpty(marka) || string.IsNullOrEmpty(canonical)) { skipped++; continue; }
                        var markaU = Normalize(marka);
                        seenBrands.Add(markaU);

                        if (!productByName.TryGetValue(Normalize(canonical), out var product))
                        {
                            // Sessiz atlama YOK — DB'de olmayan kanonik ürün açıkça raporlanır
                            unmatchedCanonical.Add(canonical);
                            unmatchedBrands.Add(markaU);
                            skipped++;
                            continue;
                        }

                        if (!aliasByProduct.TryGetValue(product.ProductId, out var list))
                            aliasByProduct[product.ProductId] = list = new List<string>();
                        if (!list.Contains(markaU)) list.Add(markaU);

                        // Ürün grubu (ilk dolu değer kazanır; UI'daki ==\"OTC\" ile eşleşsin diye büyük harf)
                        if (!string.IsNullOrEmpty(grup) && !groupByProduct.ContainsKey(product.ProductId))
                            groupByProduct[product.ProductId] = grup.ToUpperInvariant();
                    }
                    catch (Exception ex) { errors.Add($"Satır {row}: {ex.Message}"); skipped++; }
                }

                AppendUnmatched(errors, "DB'de bulunamayan kanonik ürün (Products.Name ile eşleşmiyor)", unmatchedCanonical);

                // Önizleme: her ürünün alacağı grup + alias listesi (ÜRÜN [GRUP] ← marka)
                var productById = products.ToDictionary(p => p.ProductId, p => p);
                foreach (var kv in aliasByProduct.OrderBy(k => productById[k.Key].Name))
                {
                    var grupEtiket = groupByProduct.TryGetValue(kv.Key, out var g) ? $" [{g}]" : "";
                    result.ProductMapPreview.Add($"{productById[kv.Key].Name}{grupEtiket} ← {string.Join("; ", kv.Value)}");
                }

                sw.Stop();
                result.Success = true;
                result.TotalRows = rowCount - 2;
                result.InsertedRows = aliasByProduct.Count;      // alias yazılacak ürün sayısı
                result.UpdatedRows = 0;
                result.SkippedRows = skipped;
                result.Errors = errors;
                result.ProcessingMs = (int)sw.ElapsedMilliseconds;
                result.ExpectedCount = aliasByProduct.Count;
                result.TotalBrandCount = seenBrands.Count;
                result.UnmatchedBrands = unmatchedBrands.OrderBy(x => x).ToList();
                result.MatchedBrandCount = seenBrands.Count - unmatchedBrands.Count;

                if (dryRun) return result;

                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    foreach (var kv in aliasByProduct)
                    {
                        var product = productById[kv.Key];
                        product.ImportAlias = string.Join(";", kv.Value);
                        // Ürün grubu master'dan senkronlanır (varsa)
                        if (groupByProduct.TryGetValue(kv.Key, out var grup))
                            product.ProductGroup = grup;
                    }

                    db.ImportLogs.Add(new ImportLog
                    {
                        UserId = userId,
                        FileType = "UrunMarka",
                        FileName = fileName,
                        FileHash = fileHash,
                        ImportRowCount = aliasByProduct.Count,
                        ReplacedCount = 0,
                        Status = ClassifyStatus(skipped, errors.Count),
                        ValidationErrors = SerializeErrors(errors),
                        ProcessingTimeMs = result.ProcessingMs,
                        ImportedAt = batchTime
                    });
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }

                // Teyit: alias gerçekten yazıldı mı?
                var ids = aliasByProduct.Keys.ToList();
                result.VerifiedCount = await db.Products.CountAsync(p => ids.Contains(p.ProductId) && p.ImportAlias != null && p.ImportAlias != "");
                result.Verified = result.VerifiedCount == result.ExpectedCount;

                await _audit.LogAsync(userId, "Import", "UrunMarka", null,
                    newValue: $"{fileName} — {aliasByProduct.Count} ürün alias + {groupByProduct.Count} grup güncellendi ({skipped} atl)");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
                if (!dryRun) await TryLogFailureAsync(userId, "UrunMarka", fileName, ex.Message, batchTime);
            }

            return result;
        }

        // ============================================================
        // IMPORT GERİ ALMA
        // ============================================================
        public async Task<string> RollbackImportAsync(int logId, int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            using var db = _factory.CreateDbContext();
            var log = await db.ImportLogs.FindAsync(logId);
            if (log == null) return "Log bulunamadı";

            // Tam eşleşme: import sırasında tüm satırlar ile ImportLog aynı batchTime'ı taşır.
            // Bu, ±60sn pencereli eski yöntemin yol açtığı çapraz-import silme riskini ortadan kaldırır.
            var importDate = log.ImportedAt;

            // ATOMİK: tüm silmeler + log kaldırma TEK transaction. Eski hâlinde detay/aggregate
            // silmeleri hemen commit oluyor, log silme sonra patlayınca veri gidip log kalıyordu
            // (satır sayısı tutarsızlığı). Ayrıca çift-tık yarışında ikinci çağrının "0 satır"
            // concurrency hatası ErrorBoundary'ye düşüyordu — burada zarifçe ele alınır.
            int deleted;
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();

                // Sell-Out geri alınırken GLN detayı da aynı batchTime ile temizlenir.
                if (log.FileType == "SellOut")
                    await db.SellOutDetails.Where(d => d.ImportedAt == importDate).ExecuteDeleteAsync();
                deleted = log.FileType switch
                {
                    "SellOut"     => await db.SellOuts.Where(s => s.ImportedAt == importDate).ExecuteDeleteAsync(),
                    "Sipariş"     => await db.Orders.Where(o => o.ImportedAt == importDate).ExecuteDeleteAsync(),
                    "Hedef"       => await db.Targets.Where(t => t.ImportedAt == importDate).ExecuteDeleteAsync(),
                    "Ruta"        => await db.RutaUyum.Where(r => r.ImportedAt == importDate).ExecuteDeleteAsync(),
                    "ZiyaretPlan" => await db.VisitPlans.Where(v => v.ImportedAt == importDate).ExecuteDeleteAsync(),
                    "EczanePlani" => await db.PharmacyPlans.Where(p => p.ImportedAt == importDate).ExecuteDeleteAsync(),
                    _             => 0
                };

                db.ImportLogs.Remove(log);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Yarış: log başka bir (eşzamanlı) geri alma tarafından zaten silinmiş — no-op.
                return "Bu içe aktarma zaten geri alınmış.";
            }

            await _audit.LogAsync(actorUserId, "ImportRollback", log.FileType, logId,
                oldValue: $"{log.FileName} — {deleted} satır geri alındı");
            return deleted == 0
                ? "Bu içe aktarıma ait satır bulunamadı (eski kayıtlar tam eşleşmeyebilir). Log kaldırıldı."
                : $"{deleted} satır silindi";
        }

        public async Task<string> DeleteLogAsync(int logId, int actorUserId)
        {
            await EnsureAdminAsync(actorUserId);
            using var db = _factory.CreateDbContext();
            var log = await db.ImportLogs.FindAsync(logId);
            if (log == null) return "Log bulunamadı";
            db.ImportLogs.Remove(log);
            await db.SaveChangesAsync();
            return "Log silindi";
        }
    }

    /// <summary>Hücre metnini kanonik eşleştirme anahtarına çevirir (null-güvenli uzantı).</summary>
    internal static class ImportCellExtensions
    {
        internal static string NormKey(this string? s) => ImportService.Normalize(s);
    }

    /// <summary>Önizlemede gösterilen ve tek tıkla kalıcı eşlemeye çevrilebilen öneri.</summary>
    public class MatchSuggestion
    {
        public string Type { get; set; } = "";       // Brick | User | Product
        public string Source { get; set; } = "";      // eşleşmeyen kaynak değer (normalize)
        public string Suggestion { get; set; } = "";  // en yakın aday
        public int TargetId { get; set; }             // BrickId / UserId / ProductId
    }

    public class ImportResult
    {
        public string FileName { get; set; } = "";
        public string FileType { get; set; } = "";
        public bool Success { get; set; }
        public int TotalRows { get; set; }
        public int InsertedRows { get; set; }
        public int UpdatedRows { get; set; }
        public int SkippedRows { get; set; }
        public string? Error { get; set; }
        public List<string> Errors { get; set; } = new();
        public int ProcessingMs { get; set; }

        // Çift kontrol / teyit alanları
        public bool IsDryRun { get; set; }
        public string? FileHash { get; set; }
        /// <summary>Aynı içerikli dosya daha önce yüklendiyse o yüklemenin tarihi</summary>
        public DateTime? DuplicateOf { get; set; }
        /// <summary>Yazılması beklenen satır sayısı (insert + update)</summary>
        public int ExpectedCount { get; set; }
        /// <summary>Import sonrası DB'den geri sayılan batch satır sayısı</summary>
        public int VerifiedCount { get; set; }
        /// <summary>Dosyadan okunan miktar toplamı (kutu/hedef/ziyaret)</summary>
        public decimal? FileTotal { get; set; }
        /// <summary>DB'ye yazılan batch'in miktar toplamı</summary>
        public decimal? DbTotal { get; set; }
        /// <summary>Satır sayısı + toplam birebir eşleşti mi (null = teyit yapılmadı/dry-run)</summary>
        public bool? Verified { get; set; }

        /// <summary>Eczane planı: GLN'i boş olduğu için eşleşemeyen ve atlanan satır sayısı</summary>
        public int BlankGlnRows { get; set; }

        // ── Eşleşme kapsamı (dry-run "double-check" paneli) ──────────────────────
        /// <summary>Dosyada görülen distinct marka/ürün sayısı</summary>
        public int TotalBrandCount { get; set; }
        /// <summary>Eşleşen distinct marka sayısı</summary>
        public int MatchedBrandCount { get; set; }
        /// <summary>Eşleşmeyen markalar (satışın sessizce düşmesini engelleyen kapı)</summary>
        public List<string> UnmatchedBrands { get; set; } = new();
        /// <summary>Eşleşmeyen değerler için yapısal "bunu mu demek istediniz" önerileri (dry-run)</summary>
        public List<MatchSuggestion> MatchSuggestions { get; set; } = new();
        public int TotalBrickCount { get; set; }
        public int MatchedBrickCount { get; set; }
        public int TotalPeriodCount { get; set; }
        public int MatchedPeriodCount { get; set; }

        // ── GLN detay (sell-out) ─────────────────────────────────────────────────
        /// <summary>Yazılan kurum (GLN) detay satırı sayısı</summary>
        public int DetailRowCount { get; set; }
        /// <summary>Detay GLN toplamı = aggregate brick toplamı tutarlılığı (null = kontrol yok)</summary>
        public bool? DetailConsistent { get; set; }

        // ── Ürün-Marka master (Barkod-Product) önizlemesi ────────────────────────
        /// <summary>"KANONİK ÜRÜN ← marka1; marka2" alias atama önizlemesi</summary>
        public List<string> ProductMapPreview { get; set; } = new();

        // ── Hedef: atanmamış satırlar (Mümessil='—' / Brick='-' / '(BRICK ATANMAMIŞ)') ──
        /// <summary>Atanmamış hedef satırı sayısı (bilinçli olarak skora dahil edilmez)</summary>
        public int UnassignedTargetRows { get; set; }
        /// <summary>Atanmamış satırların toplam HedefAdet'i — "şu kadar hedef ST'ye düşmüyor" şeffaflığı</summary>
        public decimal UnassignedTargetQty { get; set; }

        // ── Hedef/Brick importu: brick→ST haritası senkronu (UserBricks) ─────────────
        /// <summary>Hedef/Brick importunda UserBricks'e yazılan (yeni+yeniden aktifleştirilen) atama sayısı</summary>
        public int BrickAssignmentsSynced { get; set; }
        /// <summary>Brick→tek ST kuralı gereği pasifleştirilen eski/çakışan atama sayısı</summary>
        public int BrickAssignmentsDeactivated { get; set; }
    }
}
