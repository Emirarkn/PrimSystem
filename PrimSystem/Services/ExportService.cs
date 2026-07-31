using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using PrimSystem.Data;
using System.Drawing;

namespace PrimSystem.Services
{
    public class ExportService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;

        // Ortak biçim sabitleri (gerçek Excel sayıları → hesaplanabilir)
        private const string FmtPct = "0.0%";        // oran 0–1 saklanır, % gösterilir
        private const string FmtInt = "#,##0";       // adet/kutu
        private const string FmtTL = "#,##0 ₺";      // tutar
        private static readonly Color HeaderBg = Color.FromArgb(17, 24, 39);
        private static readonly Color HeaderFg = Color.FromArgb(241, 245, 249);
        private static readonly Color GridColor = Color.FromArgb(203, 213, 225);
        private static readonly Color TotalBg = Color.FromArgb(226, 232, 240);

        public ExportService(IDbContextFactory<AppDbContext> factory)
        {
            _factory = factory;
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        }

        // Ham hesaplama dökümü tüm ST verisini içerir — yalnızca Admin/Yonetici alabilir.
        private async Task EnsureCanExportAllAsync(int actorUserId)
        {
            using var db = _factory.CreateDbContext();
            var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == actorUserId);
            if (actor == null || actor.RoleId is not (RoleId.Admin or RoleId.Yonetici))
                throw new UnauthorizedAccessException("Bu dışa aktarım için yetkiniz yok.");
        }

        // ════════════════════════════════════════════════════════════════════
        //  ORTAK BİÇİMLENDİRME YARDIMCILARI
        // ════════════════════════════════════════════════════════════════════

        // Sayfa başlığı (1. satır, sütunlar boyunca birleştirilmiş).
        private static void WriteTitle(ExcelWorksheet ws, int lastCol, string text)
        {
            ws.Cells[1, 1].Value = text;
            ws.Cells[1, 1, 1, lastCol].Merge = true;
            ws.Cells[1, 1].Style.Font.Bold = true;
            ws.Cells[1, 1].Style.Font.Size = 13;
            ws.Cells[1, 1].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            ws.Row(1).Height = 22;
        }

        // Başlık satırını biçimle (koyu zemin, beyaz kalın, ortalı).
        private static void StyleHeaderRow(ExcelWorksheet ws, int row, int lastCol)
        {
            var range = ws.Cells[row, 1, row, lastCol];
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(HeaderBg);
            range.Style.Font.Color.SetColor(HeaderFg);
            range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            range.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            range.Style.WrapText = true;
            ws.Row(row).Height = 26;
        }

        // Tabloyu sonlandır: otomatik filtre, başlık+isim dondurma, kenarlık, sütun
        // genişliği (taban-tavan), yazdırma ayarı (yatay, sayfaya sığdır, başlık tekrarı).
        private static void Finalize(ExcelWorksheet ws, int headerRow, int lastRow, int lastCol, int freezeCol)
        {
            if (ws.Dimension == null) return;
            int firstData = headerRow + 1;

            // Otomatik filtre (Excel'de sıralama/filtreleme)
            ws.Cells[headerRow, 1, headerRow, lastCol].AutoFilter = true;

            // Tüm tabloya ince kenarlık
            var table = ws.Cells[headerRow, 1, lastRow, lastCol];
            table.Style.Border.Top.Style = ExcelBorderStyle.Hair;
            table.Style.Border.Bottom.Style = ExcelBorderStyle.Hair;
            table.Style.Border.Left.Style = ExcelBorderStyle.Hair;
            table.Style.Border.Right.Style = ExcelBorderStyle.Hair;
            table.Style.Border.Top.Color.SetColor(GridColor);
            table.Style.Border.Bottom.Color.SetColor(GridColor);
            table.Style.Border.Left.Color.SetColor(GridColor);
            table.Style.Border.Right.Color.SetColor(GridColor);

            // Sütun genişliği: otomatik + tavan (uzun adlar taşmasın)
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            for (int c = 1; c <= lastCol; c++)
            {
                if (ws.Column(c).Width > 42) ws.Column(c).Width = 42;
                if (ws.Column(c).Width < 9) ws.Column(c).Width = 9;
            }

            // Başlık altını ve isim sütununu dondur
            ws.View.FreezePanes(firstData, freezeCol + 1);

            // Yazdırma ayarı
            ws.PrinterSettings.Orientation = eOrientation.Landscape;
            ws.PrinterSettings.FitToPage = true;
            ws.PrinterSettings.FitToWidth = 1;
            ws.PrinterSettings.FitToHeight = 0;
            ws.PrinterSettings.RepeatRows = new ExcelAddress($"{headerRow}:{headerRow}");
            ws.PrinterSettings.TopMargin = 0.4M; ws.PrinterSettings.BottomMargin = 0.4M;
            ws.PrinterSettings.LeftMargin = 0.3M; ws.PrinterSettings.RightMargin = 0.3M;
        }

        // Filtreye duyarlı toplam/ortalama satırı (SUBTOTAL → Excel'de filtreleyince güncellenir).
        // cols: (sütun, "avg"|"sum") çiftleri. labelCol'a "ÖZET" yazılır.
        private static void TotalsRow(ExcelWorksheet ws, int totalRow, int firstDataRow, int lastDataRow,
            int lastCol, int labelCol, IEnumerable<(int Col, string Kind)> cols)
        {
            ws.Cells[totalRow, labelCol].Value = "ÖZET ▸ filtreye duyarlı";
            foreach (var (col, kind) in cols)
            {
                int code = kind == "sum" ? 109 : 101; // 109=SUM, 101=AVERAGE (gizli/filtreli hariç)
                var rng = ws.Cells[firstDataRow, col, lastDataRow, col].Address;
                ws.Cells[totalRow, col].Formula = $"SUBTOTAL({code},{rng})";
            }
            var band = ws.Cells[totalRow, 1, totalRow, lastCol];
            band.Style.Font.Bold = true;
            band.Style.Fill.PatternType = ExcelFillStyle.Solid;
            band.Style.Fill.BackgroundColor.SetColor(TotalBg);
            band.Style.Border.Top.Style = ExcelBorderStyle.Thin;
            band.Style.Border.Top.Color.SetColor(HeaderBg);
        }

        // ════════════════════════════════════════════════════════════════════
        //  1) RAPOR DÖKÜMÜ (Raporlar / Bölge Analizi) — sayfa zaten filtreli verir
        // ════════════════════════════════════════════════════════════════════
        public byte[] ExportReportAsync(int year, string quarter, IEnumerable<CalcSummary> summaries)
        {
            var list = summaries.ToList();
            using var package = new ExcelPackage();

            var eligible = list.Where(s => s.Calc.IsEligible).ToList();
            var passed = eligible.Where(s => s.Calc.PassedGate).ToList();

            // ── Sheet: ST Listesi ──────────────────────────────────────────────
            var wsS = package.Workbook.Worksheets.Add("ST Listesi");
            WriteTitle(wsS, 10, $"{year} {quarter} — Satış Temsilcisi Performansı");
            int hr = 2; // başlık satırı
            var headers = new[] { "Sıra", "İsim", "Bölge", "Satış %", "Ruta %", "Dağıtım %", "Sipariş %", "Final %", "Prim (₺)", "Durum" };
            for (int i = 0; i < headers.Length; i++) wsS.Cells[hr, i + 1].Value = headers[i];
            StyleHeaderRow(wsS, hr, headers.Length);

            int r = hr + 1, rank = 0;
            foreach (var s in list.OrderByDescending(x => x.Calc.FinalScore ?? x.Calc.TotalScore))
            {
                rank++;
                var c = s.Calc;
                wsS.Cells[r, 1].Value = rank;
                wsS.Cells[r, 2].Value = c.User?.FullName ?? "";
                wsS.Cells[r, 3].Value = c.User?.Region?.Name ?? "";
                wsS.Cells[r, 4].Value = (double)c.TotalScore;
                wsS.Cells[r, 5].Value = c.RutaScore.HasValue ? (double)c.RutaScore.Value : (object)DBNull.Value;
                wsS.Cells[r, 6].Value = c.DistScore.HasValue ? (double)c.DistScore.Value : (object)DBNull.Value;
                wsS.Cells[r, 7].Value = c.OrderScore.HasValue ? (double)c.OrderScore.Value : (object)DBNull.Value;
                wsS.Cells[r, 8].Value = (double)(c.FinalScore ?? c.TotalScore);
                wsS.Cells[r, 9].Value = (double)(c.PrimAmount ?? 0);
                wsS.Cells[r, 10].Value = !c.IsEligible ? "Prim Dışı" : c.PassedGate ? "Geçti" : "Kaldı";

                var rowColor = !c.IsEligible ? Color.FromArgb(255, 251, 235)
                    : c.PassedGate ? Color.FromArgb(220, 252, 231) : Color.FromArgb(254, 226, 226);
                wsS.Cells[r, 1, r, 10].Style.Fill.PatternType = ExcelFillStyle.Solid;
                wsS.Cells[r, 1, r, 10].Style.Fill.BackgroundColor.SetColor(rowColor);
                r++;
            }
            int lastData = r - 1;
            if (lastData >= hr + 1)
            {
                wsS.Cells[hr + 1, 4, lastData, 8].Style.Numberformat.Format = FmtPct;
                wsS.Cells[hr + 1, 9, lastData, 9].Style.Numberformat.Format = FmtTL;
                int tr = lastData + 1;
                TotalsRow(wsS, tr, hr + 1, lastData, 10, 2,
                    new[] { (4, "avg"), (5, "avg"), (6, "avg"), (7, "avg"), (8, "avg"), (9, "sum") });
                wsS.Cells[tr, 4, tr, 8].Style.Numberformat.Format = FmtPct;
                wsS.Cells[tr, 9, tr, 9].Style.Numberformat.Format = FmtTL;
                Finalize(wsS, hr, tr, 10, 2);
            }
            else Finalize(wsS, hr, hr, 10, 2);

            // ── Sheet: Bölge Özeti ─────────────────────────────────────────────
            var wsR = package.Workbook.Worksheets.Add("Bölge Özeti");
            WriteTitle(wsR, 8, $"{year} {quarter} — Bölge Özeti");
            var hdrR = new[] { "Bölge", "ST Sayısı", "Geçen", "Kalan", "Prim Dışı", "Geçiş %", "Ort. Skor", "Toplam Prim (₺)" };
            for (int i = 0; i < hdrR.Length; i++) wsR.Cells[hr, i + 1].Value = hdrR[i];
            StyleHeaderRow(wsR, hr, hdrR.Length);

            r = hr + 1;
            foreach (var g in list.GroupBy(s => s.Calc.User?.Region?.Name ?? "Belirsiz").OrderBy(g => g.Key))
            {
                var eli = g.Where(s => s.Calc.IsEligible).ToList();
                var passedCnt = eli.Count(s => s.Calc.PassedGate);
                wsR.Cells[r, 1].Value = g.Key;
                wsR.Cells[r, 2].Value = g.Count();
                wsR.Cells[r, 3].Value = passedCnt;
                wsR.Cells[r, 4].Value = eli.Count(s => !s.Calc.PassedGate);
                wsR.Cells[r, 5].Value = g.Count(s => !s.Calc.IsEligible);
                wsR.Cells[r, 6].Value = eli.Any() ? (double)passedCnt / eli.Count : 0;
                wsR.Cells[r, 7].Value = eli.Any() ? eli.Average(s => (double)(s.Calc.FinalScore ?? s.Calc.TotalScore)) : 0;
                wsR.Cells[r, 8].Value = (double)g.Sum(s => s.Calc.PrimAmount ?? 0);
                r++;
            }
            lastData = r - 1;
            if (lastData >= hr + 1)
            {
                wsR.Cells[hr + 1, 6, lastData, 7].Style.Numberformat.Format = FmtPct;
                wsR.Cells[hr + 1, 8, lastData, 8].Style.Numberformat.Format = FmtTL;
                wsR.Cells[hr + 1, 2, lastData, 5].Style.Numberformat.Format = FmtInt;
                int tr = lastData + 1;
                TotalsRow(wsR, tr, hr + 1, lastData, 8, 1,
                    new[] { (2, "sum"), (3, "sum"), (4, "sum"), (5, "sum"), (6, "avg"), (7, "avg"), (8, "sum") });
                wsR.Cells[tr, 2, tr, 5].Style.Numberformat.Format = FmtInt;
                wsR.Cells[tr, 6, tr, 7].Style.Numberformat.Format = FmtPct;
                wsR.Cells[tr, 8, tr, 8].Style.Numberformat.Format = FmtTL;
                Finalize(wsR, hr, tr, 8, 1);
            }
            else Finalize(wsR, hr, hr, 8, 1);

            // ── Sheet: Ürün Analizi ────────────────────────────────────────────
            var wsP = package.Workbook.Worksheets.Add("Ürün Analizi");
            WriteTitle(wsP, 7, $"{year} {quarter} — Ürün Analizi");
            var hdrP = new[] { "Ürün", "Grup", "Ort. Hedef", "Ort. Gerçekleşen", "Ort. Oran %", "Hedefe Ulaşan ST", "Toplam ST" };
            for (int i = 0; i < hdrP.Length; i++) wsP.Cells[hr, i + 1].Value = hdrP[i];
            StyleHeaderRow(wsP, hr, hdrP.Length);

            var allDetails = list.SelectMany(s => s.Details).ToList();
            r = hr + 1;
            foreach (var g in allDetails
                .GroupBy(d => new { d.ProductId, Name = d.Product?.Name ?? "", Group = d.Product?.ProductGroup ?? "" })
                .OrderByDescending(g => g.Any() ? g.Average(d => (double)d.Rate) : 0))
            {
                wsP.Cells[r, 1].Value = g.Key.Name;
                wsP.Cells[r, 2].Value = g.Key.Group;
                wsP.Cells[r, 3].Value = g.Any() ? Math.Round(g.Average(d => (double)d.TargetQty), 0) : 0;
                wsP.Cells[r, 4].Value = g.Any() ? Math.Round(g.Average(d => (double)d.ActualQty), 0) : 0;
                wsP.Cells[r, 5].Value = g.Any() ? g.Average(d => (double)d.Rate) : 0;
                wsP.Cells[r, 6].Value = g.Count(d => d.Rate >= 1.0m);
                wsP.Cells[r, 7].Value = g.Count();
                r++;
            }
            lastData = r - 1;
            if (lastData >= hr + 1)
            {
                wsP.Cells[hr + 1, 3, lastData, 4].Style.Numberformat.Format = FmtInt;
                wsP.Cells[hr + 1, 5, lastData, 5].Style.Numberformat.Format = FmtPct;
                wsP.Cells[hr + 1, 6, lastData, 7].Style.Numberformat.Format = FmtInt;
            }
            Finalize(wsP, hr, Math.Max(lastData, hr), 7, 1);

            // ── Sheet: Özet (en başa alınır) ───────────────────────────────────
            var wsO = package.Workbook.Worksheets.Add("Özet");
            wsO.Cells[1, 1].Value = $"{year} {quarter} — Prim Özeti";
            wsO.Cells[1, 1, 1, 2].Merge = true;
            wsO.Cells[1, 1].Style.Font.Bold = true;
            wsO.Cells[1, 1].Style.Font.Size = 15;
            wsO.Row(1).Height = 26;

            var summaryRows = new (string Label, double Val, string Fmt)[]
            {
                ("Toplam ST", list.Count, FmtInt),
                ("Uygun (Eligible)", eligible.Count, FmtInt),
                ("Geçen", passed.Count, FmtInt),
                ("Kalan", eligible.Count - passed.Count, FmtInt),
                ("Prim Dışı (Yeni)", list.Count(s => !s.Calc.IsEligible), FmtInt),
                ("Geçiş Oranı", eligible.Any() ? (double)passed.Count / eligible.Count : 0, FmtPct),
                ("Ortalama Skor", eligible.Any() ? eligible.Average(s => (double)(s.Calc.FinalScore ?? s.Calc.TotalScore)) : 0, FmtPct),
                ("Toplam Prim (₺)", (double)list.Sum(s => s.Calc.PrimAmount ?? 0), FmtTL),
            };
            r = 3;
            foreach (var (label, val, fmt) in summaryRows)
            {
                wsO.Cells[r, 1].Value = label;
                wsO.Cells[r, 2].Value = val;
                wsO.Cells[r, 1].Style.Font.Bold = true;
                wsO.Cells[r, 2].Style.Numberformat.Format = fmt;
                wsO.Cells[r, 1, r, 2].Style.Border.Bottom.Style = ExcelBorderStyle.Hair;
                wsO.Cells[r, 1, r, 2].Style.Border.Bottom.Color.SetColor(GridColor);
                r++;
            }
            wsO.Column(1).Width = 24;
            wsO.Column(2).Width = 16;
            package.Workbook.Worksheets.MoveToStart("Özet");

            return package.GetAsByteArray();
        }

        // ════════════════════════════════════════════════════════════════════
        //  2) HAM HESAPLAMA DÖKÜMÜ (Admin/Yönetici) — DİKEY: her satır = ST × ürün
        // ════════════════════════════════════════════════════════════════════
        public async Task<byte[]> ExportCalculationsAsync(int year, string quarter, int actorUserId)
        {
            await EnsureCanExportAllAsync(actorUserId);
            using var db = _factory.CreateDbContext();
            var calcs = await db.Calculations
                .Include(c => c.User).ThenInclude(u => u!.Region)
                .Where(c => c.Year == year && c.Quarter == quarter)
                .OrderByDescending(c => c.FinalScore)
                .ToListAsync();

            var calcIds = calcs.Select(c => c.CalcId).ToList();
            var allDetails = await db.CalculationDetails
                .Include(d => d.Product)
                .Where(d => calcIds.Contains(d.CalcId))
                .OrderBy(d => d.Product!.SortOrder)
                .ToListAsync();
            var detailsByCalc = allDetails.GroupBy(d => d.CalcId).ToDictionary(g => g.Key, g => g.ToList());

            var summaries = calcs.Select(c => new CalcSummary
            {
                Calc = c,
                Details = detailsByCalc.TryGetValue(c.CalcId, out var d) ? d : new()
            }).ToList();

            return BuildCalculationsVertical(year, quarter, summaries);
        }

        /// <summary>
        /// Dikey (uzun/"tidy") ham döküm: her satır = 1 ST × 1 ürün. ST-seviyesi kolonlar
        /// (Bölge…Prim) o ST'nin her ürün satırında tekrar eder. Başlıklar 1. satırdadır
        /// (üst başlık YOK) — böylece Excel'de Pivot Tablo / Power Query aralık seçmeden çalışır.
        /// DB'ye dokunmaz; yalnız verilen veriyi biçimler (birim testlenebilir).
        /// </summary>
        public byte[] BuildCalculationsVertical(int year, string quarter, IEnumerable<CalcSummary> summaries)
        {
            var list = summaries.ToList();
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add($"{year}-{quarter}");

            var headers = new[]
            {
                "ST Adı", "Ürün", "Hedef", "Gerçekleşen", "Gerç. %",
                "Bölge", "Durum", "Satış %", "Ruta %", "Dağıtım %", "Sipariş %", "Final %", "Geçit", "Prim (₺)"
            };
            const int hr = 1;                 // başlık satırı (üst başlık yok)
            int lastCol = headers.Length;     // 14
            for (int i = 0; i < headers.Length; i++) ws.Cells[hr, i + 1].Value = headers[i];
            StyleHeaderRow(ws, hr, lastCol);

            int row = hr + 1;
            foreach (var s in list)
            {
                var c = s.Calc;
                var stName = c.User?.FullName ?? "";
                var region = c.User?.Region?.Name ?? "";
                var durum = !c.IsEligible ? "Prim Dışı" : c.PassedGate ? "Geçti" : "Kaldı";
                var gecit = c.PassedGate ? "EVET" : "HAYIR";
                var rowColor = !c.IsEligible ? Color.FromArgb(255, 251, 235)
                    : c.PassedGate ? Color.FromArgb(220, 252, 231) : Color.FromArgb(254, 226, 226);

                foreach (var d in s.Details.OrderBy(x => x.Product?.SortOrder ?? 0))
                {
                    int col = 1;
                    ws.Cells[row, col++].Value = stName;
                    ws.Cells[row, col++].Value = d.Product?.Name ?? "";
                    // Yanıltıcı 0 yazma — hedefi olmayan üründe Hedef ve oran boş kalır (eski yatay düzenle aynı kural).
                    ws.Cells[row, col++].Value = d.TargetQty > 0 ? (double)d.TargetQty : (object)DBNull.Value;
                    ws.Cells[row, col++].Value = d.ActualQty != 0 ? (double)d.ActualQty : (object)DBNull.Value;
                    ws.Cells[row, col++].Value = d.TargetQty > 0 ? (double)d.Rate : (object)DBNull.Value;
                    // ST-seviyesi kolonlar her ürün satırında tekrar eder (filtre/pivot için şart).
                    ws.Cells[row, col++].Value = region;
                    ws.Cells[row, col++].Value = durum;
                    ws.Cells[row, col++].Value = (double)c.TotalScore;
                    ws.Cells[row, col++].Value = c.RutaScore.HasValue ? (double)c.RutaScore.Value : (object)DBNull.Value;
                    ws.Cells[row, col++].Value = c.DistScore.HasValue ? (double)c.DistScore.Value : (object)DBNull.Value;
                    ws.Cells[row, col++].Value = c.OrderScore.HasValue ? (double)c.OrderScore.Value : (object)DBNull.Value;
                    ws.Cells[row, col++].Value = (double)(c.FinalScore ?? c.TotalScore);
                    ws.Cells[row, col++].Value = gecit;
                    ws.Cells[row, col++].Value = (double)(c.PrimAmount ?? 0);

                    ws.Cells[row, 1, row, lastCol].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws.Cells[row, 1, row, lastCol].Style.Fill.BackgroundColor.SetColor(rowColor);
                    row++;
                }
            }
            int lastData = row - 1;

            if (lastData >= hr + 1)
            {
                ws.Cells[hr + 1, 3, lastData, 4].Style.Numberformat.Format = FmtInt;   // Hedef, Gerçekleşen
                ws.Cells[hr + 1, 5, lastData, 5].Style.Numberformat.Format = FmtPct;   // Gerç. %
                ws.Cells[hr + 1, 8, lastData, 12].Style.Numberformat.Format = FmtPct;  // Satış…Final %
                ws.Cells[hr + 1, 14, lastData, 14].Style.Numberformat.Format = FmtTL;  // Prim

                // ÖZET yalnız ÜRÜN-seviyesi kolonlar için. ST-seviyesi kolonlar her ürün satırında
                // tekrar ettiğinden (13 ürün → Prim 13 kez) toplanırsa gerçeğin katı çıkardı; boş bırakılır.
                int tr = lastData + 1;
                TotalsRow(ws, tr, hr + 1, lastData, lastCol, 1,
                    new List<(int, string)> { (3, "sum"), (4, "sum"), (5, "avg") });
                ws.Cells[tr, 3, tr, 4].Style.Numberformat.Format = FmtInt;
                ws.Cells[tr, 5, tr, 5].Style.Numberformat.Format = FmtPct;
                ws.Cells[tr, 1].AddComment(
                    "ST bazlı kolonlar (Satış/Ruta/Dağıtım/Sipariş/Final % ve Prim ₺) her ürün satırında " +
                    "tekrar ettiği için burada toplanmaz — ST toplamları için Pivot Tablo kullanın.",
                    "PrimSystem");
                Finalize(ws, hr, tr, lastCol, 2);
            }
            else Finalize(ws, hr, hr, lastCol, 2);

            return package.GetAsByteArray();
        }

        // ════════════════════════════════════════════════════════════════════
        //  3) ECZANE DÖKÜMÜ — sayfa role'e göre filtreli satırları verir
        // ════════════════════════════════════════════════════════════════════
        public byte[] ExportPharmacies(int year, string quarter, IEnumerable<PharmacyRow> rows)
        {
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add($"Eczaneler {year}-{quarter}");
            var headers = new[] { "Brick", "Eczane (Kurum)", "İlçe", "İl", "GLN", "Kutu", "TL" };
            WriteTitle(ws, headers.Length, $"{year} {quarter} — Eczane (Sell-Out) Dökümü");
            int hr = 2;
            for (int i = 0; i < headers.Length; i++) ws.Cells[hr, i + 1].Value = headers[i];
            StyleHeaderRow(ws, hr, headers.Length);

            int r = hr + 1;
            foreach (var p in rows.OrderBy(x => x.BrickName).ThenByDescending(x => x.Boxes))
            {
                ws.Cells[r, 1].Value = p.BrickName;
                ws.Cells[r, 2].Value = string.IsNullOrWhiteSpace(p.KurumName) ? p.GLN : p.KurumName;
                ws.Cells[r, 3].Value = p.Ilce;
                ws.Cells[r, 4].Value = p.Il;
                ws.Cells[r, 5].Value = p.GLN;
                ws.Cells[r, 6].Value = (double)p.Boxes;
                ws.Cells[r, 7].Value = (double)p.AmountTL;
                if ((r - hr) % 2 == 0) // bantlama (okunabilirlik)
                {
                    ws.Cells[r, 1, r, 7].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws.Cells[r, 1, r, 7].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(248, 250, 252));
                }
                r++;
            }
            int lastData = r - 1;
            if (lastData >= hr + 1)
            {
                ws.Cells[hr + 1, 6, lastData, 6].Style.Numberformat.Format = FmtInt;
                ws.Cells[hr + 1, 7, lastData, 7].Style.Numberformat.Format = FmtTL;
                // GLN metin kalsın (bilimsel gösterim/yuvarlama olmasın)
                ws.Cells[hr + 1, 5, lastData, 5].Style.Numberformat.Format = "@";

                int tr = lastData + 1;
                TotalsRow(ws, tr, hr + 1, lastData, 7, 1, new[] { (6, "sum"), (7, "sum") });
                ws.Cells[tr, 6].Style.Numberformat.Format = FmtInt;
                ws.Cells[tr, 7].Style.Numberformat.Format = FmtTL;
                Finalize(ws, hr, tr, 7, 2);
            }
            else Finalize(ws, hr, hr, 7, 2);

            return package.GetAsByteArray();
        }

        // ════════════════════════════════════════════════════════════════════
        //  4) BRICK YAPISI — brick → atanmış ST(ler) + pay. Sayfa CanManage-only.
        //     Ortak brick birden çok satır (her sahip); atanmamış brick "Atanmamış" satırı.
        // ════════════════════════════════════════════════════════════════════
        public byte[] ExportBricks(IEnumerable<BrickExportRow> rows)
        {
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("Brick Yapısı");
            var headers = new[] { "Brick Adı", "Şehir", "Bölge", "Atanmış ST", "Pay (%)", "Ortak" };
            WriteTitle(ws, headers.Length, $"Brick Yapısı — Brick / Atanmış ST ({DateTime.Now:dd.MM.yyyy})");
            int hr = 2;
            for (int i = 0; i < headers.Length; i++) ws.Cells[hr, i + 1].Value = headers[i];
            StyleHeaderRow(ws, hr, headers.Length);

            int r = hr + 1;
            foreach (var b in rows.OrderBy(x => x.Brick).ThenByDescending(x => x.SharePercent ?? 0m))
            {
                ws.Cells[r, 1].Value = b.Brick;
                ws.Cells[r, 2].Value = b.City;
                ws.Cells[r, 3].Value = b.Region;
                ws.Cells[r, 4].Value = b.ST;
                if (b.SharePercent.HasValue) ws.Cells[r, 5].Value = (double)b.SharePercent.Value;
                ws.Cells[r, 6].Value = b.Shared ? "Evet" : "—";
                if ((r - hr) % 2 == 0) // bantlama
                {
                    ws.Cells[r, 1, r, headers.Length].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws.Cells[r, 1, r, headers.Length].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(248, 250, 252));
                }
                r++;
            }
            int lastData = r - 1;
            if (lastData >= hr + 1)
            {
                ws.Cells[hr + 1, 5, lastData, 5].Style.Numberformat.Format = "0.#";
                Finalize(ws, hr, lastData, headers.Length, 1);
            }
            else Finalize(ws, hr, hr, headers.Length, 1);

            return package.GetAsByteArray();
        }

        // ════════════════════════════════════════════════════════════════════
        //  İÇE AKTARMA ŞABLONLARI (girdi — biçim değiştirilmez)
        // ════════════════════════════════════════════════════════════════════
        public byte[] GetImportTemplate(string type)
        {
            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Şablon");

            string[] headers;
            int headerRow;
            object[] sampleRow;

            switch (type)
            {
                case "SellOut":
                    headerRow = 2;
                    headers = new[] { "Yıl", "Ay", "İl", "İlçe", "Brick", "Kurum GLN", "Kurum", "Marka", "Toplam KUTU", "Ana Mal TL (Net İSF)", "MF TL (Net İSF)" };
                    sampleRow = new object[] { 2025, "2025-01", "İL_ADI", "İLÇE_ADI", "BRICK_ADI", "1234567890123", "KURUM_ADI", "MARKA_ADI", 100, 5000.00, 1000.00 };
                    break;
                case "UrunMarka":
                    headerRow = 2;
                    headers = new[] { "Barkod", "Marka", "Ürün", "Product", "Urun Grubu" };
                    sampleRow = new object[] { "3574660039559", "MARKA_ADI (InfiniLens)", "TAM SKU ADI", "KANONİK_ÜRÜN_ADI", "ITR" };
                    break;
                case "Siparis":
                    headerRow = 2;
                    headers = new[] { "Yıl", "Ay", "Ana Depo", "Brick", "Personel", "Marka", "Toplam KUTU", "Ana Mal TL", "MF TL", "Kurum Adedi" };
                    sampleRow = new object[] { 2025, "2025-01", "DEPO_ADI", "BRICK_ADI", "PERSONEL_ADI", "MARKA_ADI", 50, 2500.00, 500.00, 10 };
                    break;
                case "Hedef":
                    headerRow = 1;
                    headers = new[] { "Ay", "Mümessil", "Brick", "ÜrünGrubu", "Ürün", "HedefAdet" };
                    sampleRow = new object[] { 1, "PERSONEL_ADI", "BRICK_ADI", "ÜRÜN_GRUBU", "ÜRÜN_ADI", 100 };
                    break;
                case "Ruta":
                    headerRow = 1;
                    headers = new[] { "Personel", "PlanlananZiyaret", "GerceklenenZiyaret" };
                    sampleRow = new object[] { "PERSONEL_ADI", 20, 18 };
                    break;
                case "BrickAtama":
                    headerRow = 1;
                    headers = new[] { "Brick", "Mümessil" };
                    sampleRow = new object[] { "BRICK_ADI", "PERSONEL_ADI" };
                    break;
                case "ZiyaretPlani":
                    headerRow = 1;
                    headers = new[] { "Bölge", "Kadro", "ÜTT", "Tarih", "İl", "Brick", "GLN" };
                    sampleRow = new object[] { "BÖLGE_ADI", "PERSONEL_ADI", "PERSONEL_ADI", "01.01.2025", "İL_ADI", "BRICK_ADI", "1234567890123" };
                    break;
                case "EczanePlani":
                    // UTT_Brick raporunun ECZANE sayfası. Import kolonları ADINA göre çözer;
                    // kolon sırası/fazlalığı önemli değil, zorunlu olanlar: Brick · GLN · Plan.
                    headerRow = 1;
                    headers = new[] { "Ekip", "Bölge", "Çalışan ÜTT", "Brick", "Segment", "Uzmanlık",
                                      "Ünvan", "SMM ID", "SMM Adı Soyadı", "Kurum", "GLN", "Frek.", "Plan", "Ziyaret" };
                    sampleRow = new object[] { "SAHA SATIŞ EKİBİ", "BÖLGE_ADI", "PERSONEL_ADI", "BRICK_ADI", "A(2)", "ECZACI",
                                               "-", "123456", "ECZACI_ADI", "ECZANE_ADI", "1234567890123", 1, 6, 5 };
                    break;
                default:
                    return Array.Empty<byte>();
            }

            for (int i = 0; i < headers.Length; i++)
                ws.Cells[headerRow, i + 1].Value = headers[i];

            using (var hRange = ws.Cells[headerRow, 1, headerRow, headers.Length])
            {
                hRange.Style.Font.Bold = true;
                hRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
                hRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(59, 130, 246));
                hRange.Style.Font.Color.SetColor(Color.White);
            }

            int sampleRow2 = headerRow + 1;
            for (int i = 0; i < sampleRow.Length; i++)
                ws.Cells[sampleRow2, i + 1].Value = sampleRow[i];

            using (var sRange = ws.Cells[sampleRow2, 1, sampleRow2, headers.Length])
            {
                sRange.Style.Font.Italic = true;
                sRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
                sRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(30, 41, 59));
                sRange.Style.Font.Color.SetColor(Color.FromArgb(100, 116, 139));
            }

            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            return pkg.GetAsByteArray();
        }
    }

    // Brick yapısı export satırı: bir brick + (varsa) bir atanmış ST + payı.
    // Ortak brick birden çok satır (her sahip); atanmamış brick ST="Atanmamış", SharePercent=null.
    public record BrickExportRow(string Brick, string City, string Region, string ST, decimal? SharePercent, bool Shared);
}
