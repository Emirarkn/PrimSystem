using OfficeOpenXml;
using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class ImportValidationTests
    {
        static ImportValidationTests()
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        }

        private static ExcelWorksheet MakeSheet(int headerRow, params string[] headers)
        {
            var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("Test");
            for (int i = 0; i < headers.Length; i++)
                ws.Cells[headerRow, i + 1].Value = headers[i];
            // Veri satırı — Dimension'ın oluşması için
            ws.Cells[headerRow + 1, 1].Value = "veri";
            return ws;
        }

        // ── ClassifyStatus ──────────────────────────────────────────────

        [Theory]
        [InlineData(0, 0, "Success")]
        [InlineData(5, 0, "Partial")]
        [InlineData(0, 3, "Partial")]
        [InlineData(2, 2, "Partial")]
        public void ClassifyStatus_SkipVeyaHataVarsa_Partial(int skipped, int errors, string expected)
        {
            Assert.Equal(expected, ImportService.ClassifyStatus(skipped, errors));
        }

        // ── AmountsVerified (teyit toleransı — decimal(18,4) yuvarlama) ──────

        [Fact]
        public void AmountsVerified_TamEsit_True()
            => Assert.True(ImportService.AmountsVerified(1000m, 1000m, 100));

        [Fact]
        public void AmountsVerified_KucukYuvarlamaFarki_True()
            // 11748 satır × decimal(18,4) → ~0,5 birim fark; tolerans max(1, 1.17)=1.17 → geçer
            => Assert.True(ImportService.AmountsVerified(1568566.5400m, 1568566.5361m, 11748));

        [Fact]
        public void AmountsVerified_BuyukFark_False()
            => Assert.False(ImportService.AmountsVerified(9000m, 10000m, 100));

        [Fact]
        public void AmountsVerified_NullGuvenli()
            => Assert.True(ImportService.AmountsVerified(null, null, 0));

        // ── ValidateHeader ──────────────────────────────────────────────

        [Fact]
        public void ValidateHeader_DogruBasliklar_NullDoner()
        {
            using var ws = MakeSheet(1, "Ay", "Mümessil", "Brick", "Ürün Grubu", "Ürün", "Hedef Adet");
            var err = ImportService.ValidateHeader(ws, 1,
                new[] { (1, "Ay"), (2, "Mümessil"), (3, "Brick"), (6, "Hedef") }, "Hedef");
            Assert.Null(err);
        }

        [Fact]
        public void ValidateHeader_BuyukKucukVeIcermeToleransli()
        {
            using var ws = MakeSheet(1, "AY", "mümessil adı", "BRICK KODU");
            var err = ImportService.ValidateHeader(ws, 1,
                new[] { (1, "Ay"), (2, "Mümessil"), (3, "Brick") }, "Hedef");
            Assert.Null(err);
        }

        [Fact]
        public void ValidateHeader_YanlisDosya_HataDoner()
        {
            // Sipariş dosyası başlıkları, Hedef şablonuna karşı doğrulanıyor
            using var ws = MakeSheet(1, "Yıl", "Dönem", "Ana Depo", "Bölge", "Satıcı", "Tutar");
            var err = ImportService.ValidateHeader(ws, 1,
                new[] { (1, "Ay"), (2, "Mümessil"), (3, "Brick"), (6, "Hedef") }, "Hedef");
            Assert.NotNull(err);
            Assert.Contains("Hedef", err);
            Assert.Contains("şablonuna uymuyor", err);
        }

        [Fact]
        public void ValidateHeader_BosBaslikSatiri_HataDoner()
        {
            using var ws = MakeSheet(1); // hiç başlık yok
            var err = ImportService.ValidateHeader(ws, 1,
                new[] { (1, "Personel"), (2, "Plan"), (3, "Ger") }, "Ruta Uyum");
            Assert.NotNull(err);
            Assert.Contains("(boş)", err);
        }

        [Fact]
        public void ValidateHeader_IkiEslesmeYeterli()
        {
            // 4 beklenenden yalnızca 2'si eşleşiyor — başlık varyasyonu toleransı
            using var ws = MakeSheet(2, "X", "Ay", "Brick Adı", "Başka", "Kolon");
            var err = ImportService.ValidateHeader(ws, 2,
                new[] { (2, "Ay"), (3, "Brick"), (4, "Marka"), (5, "Kutu") }, "Sell-Out");
            Assert.Null(err);
        }

        [Fact]
        public void ValidateHeader_TekEslesme_Yetersiz()
        {
            using var ws = MakeSheet(2, "X", "Ay", "Yanlış", "Kolonlar", "Burada");
            var err = ImportService.ValidateHeader(ws, 2,
                new[] { (2, "Ay"), (3, "Brick"), (4, "Marka"), (5, "Kutu") }, "Sell-Out");
            Assert.NotNull(err);
        }

        // ── Yeni Sell-Out (11-kolon InfiniLens) başlık düzeni ────────────

        [Fact]
        public void ValidateHeader_YeniSellOut_DogruDuzen_Gecer()
        {
            // Gerçek InfiniLens DistInvTran başlığı (Satır 2): kolonlar kaymış
            using var ws = MakeSheet(2, "Yıl", "Ay", "İl", "İlçe", "Brick",
                "Kurum GLN", "Kurum", "Marka", "Toplam KUTU", "Ana Mal TL (Net İSF)", "MF TL (Net İSF)");
            var err = ImportService.ValidateHeader(ws, 2,
                new[] { (2, "Ay"), (5, "Brick"), (8, "Marka"), (9, "Kutu") }, "Sell-Out", minMatch: 3);
            Assert.Null(err);
        }

        [Fact]
        public void ValidateHeader_YeniSellOut_EskiDuzen_Reddedilir()
        {
            // Eski 7-kolon Sell-Out (Brick@3) yeni düzene (Brick@5/Marka@8/Kutu@9) karşı → red
            using var ws = MakeSheet(2, "Yıl", "Ay", "Brick", "Marka", "ToplamKutu", "AnaMalTL", "MFTL");
            var err = ImportService.ValidateHeader(ws, 2,
                new[] { (2, "Ay"), (5, "Brick"), (8, "Marka"), (9, "Kutu") }, "Sell-Out", minMatch: 3);
            Assert.NotNull(err);
            Assert.Contains("şablonuna uymuyor", err);
        }

        [Fact]
        public void ValidateHeader_MinMatch_UcGerekliyse_IkiEslesme_Yetersiz()
        {
            // Yalnız Ay@2 + Brick@5 eşleşir (Marka@8/Kutu@9 boş) → 2 eşleşme
            using var ws = MakeSheet(2, "Yıl", "Ay", "İl", "İlçe", "Brick");
            var errStrict = ImportService.ValidateHeader(ws, 2,
                new[] { (2, "Ay"), (5, "Brick"), (8, "Marka"), (9, "Kutu") }, "Sell-Out", minMatch: 3);
            Assert.NotNull(errStrict);                 // 3 gerekiyor → red
            var errLoose = ImportService.ValidateHeader(ws, 2,
                new[] { (2, "Ay"), (5, "Brick"), (8, "Marka"), (9, "Kutu") }, "Sell-Out");
            Assert.Null(errLoose);                     // varsayılan 2 → geçer
        }

        [Fact]
        public void ValidateHeader_UrunMarka_DogruBasliklar_Gecer()
        {
            using var ws = MakeSheet(2, "Barkod", "Marka", "Ürün", "Product");
            var err = ImportService.ValidateHeader(ws, 2,
                new[] { (2, "Marka"), (3, "Ürün"), (4, "Product") }, "Ürün-Marka Eşleştirme", minMatch: 2);
            Assert.Null(err);
        }

        // ── SplitAliases ────────────────────────────────────────────────

        [Fact]
        public void SplitAliases_NullVeyaBos_BosDoner()
        {
            Assert.Empty(ImportService.SplitAliases(null));
            Assert.Empty(ImportService.SplitAliases("   "));
        }

        [Fact]
        public void SplitAliases_TekDeger_TekOge_BuyukHarfTrim()
        {
            var r = ImportService.SplitAliases("  Carefree ");
            Assert.Equal(new[] { "CAREFREE" }, r);
        }

        [Fact]
        public void SplitAliases_CokluMarka_AyracVeBosluklariTemizler()
        {
            var r = ImportService.SplitAliases("JOHNSON'S BABY;NEUTROGENA | carefree\nMAUI").ToList();
            Assert.Equal(new[] { "JOHNSON'S BABY", "NEUTROGENA", "CAREFREE", "MAUI" }, r);
        }

        // ── BuildProductMap ─────────────────────────────────────────────

        [Fact]
        public void BuildProductMap_NameOncelikli_AliasEzemez()
        {
            // Ürün A'nın adı, Ürün B'nin alias'ıyla çakışırsa Name kazanır.
            var products = new (int, string, string?)[]
            {
                (1, "VISINE", null),
                (2, "Başka Ürün", "VISINE"),
            };
            var map = ImportService.BuildProductMap(products);
            Assert.Equal(1, map["VISINE"]);
        }

        [Fact]
        public void BuildProductMap_NullAliasUrunDahil_NameIleEslesir()
        {
            var products = new (int, string, string?)[] { (5, "LISTERINE", null) };
            var map = ImportService.BuildProductMap(products);
            Assert.Equal(5, map["LISTERINE"]);
        }

        [Fact]
        public void BuildProductMap_CokluAlias_HepsiAyniUruneGider()
        {
            // DİĞER İTRİYAT senaryosu: dört marka tek ürüne toplanır.
            var products = new (int, string, string?)[]
            {
                (13, "DİĞER İTRİYAT (JB GRUBU / NT GRUBU)", "JOHNSON'S BABY;NEUTROGENA;CAREFREE;AVEENO"),
            };
            var map = ImportService.BuildProductMap(products);
            Assert.Equal(13, map["JOHNSON'S BABY"]);
            Assert.Equal(13, map["NEUTROGENA"]);
            Assert.Equal(13, map["CAREFREE"]);
            Assert.Equal(13, map["AVEENO"]);
            Assert.Equal(13, map[ImportService.Normalize("DİĞER İTRİYAT (JB GRUBU / NT GRUBU)")]);
        }

        [Fact]
        public void BuildProductMap_PasifUrunListeDisiysaAliasKazanir()
        {
            // Aveeno birleştirmesi: P1 pasifleştirildiği için import ürün listesine hiç
            // girmez (ImportService.cs:1303-1306 → .Where(p => p.IsActive)); dolayısıyla
            // "AVEENO" adı Name olarak haritaya yazılmaz ve P13'ün alias'ı kazanır.
            // Eğer pasif ürün listeye sızarsa Name önceliği alias'ı ezer ve Aveeno
            // hacmi tekrar prim dışına düşer — bu test o regresyonu yakalar.
            var aktifUrunler = new (int, string, string?)[]
            {
                (13, "DİĞER İTRİYAT (JB GRUBU / NT GRUBU)", "JOHNSON'S BABY;NEUTROGENA;CAREFREE;AVEENO"),
            };
            Assert.Equal(13, ImportService.BuildProductMap(aktifUrunler)["AVEENO"]);

            // Pasif ürün de listeye girerse Name kazanır — istenmeyen durum.
            var pasifSizinti = new (int, string, string?)[]
            {
                (1, "Aveeno", "AVEENO"),
                (13, "DİĞER İTRİYAT (JB GRUBU / NT GRUBU)", "JOHNSON'S BABY;NEUTROGENA;CAREFREE;AVEENO"),
            };
            Assert.Equal(1, ImportService.BuildProductMap(pasifSizinti)["AVEENO"]);
        }

        // ── ParseCellNumber (biçim/kültür duyarsız sayısal okuma) ───────────

        [Fact]
        public void ParseCellNumber_HamSayisalDeger_DogrudanKullanilir()
        {
            // EPPlus .Value sayısalsa, yanıltıcı .Text'e ("1.538") bakılmaz.
            Assert.Equal(1538.4778m, ImportService.ParseCellNumber(1538.4778d, "1.538"));
            Assert.Equal(1538m, ImportService.ParseCellNumber(1538, "1.538"));
            Assert.Equal(16763.538m, ImportService.ParseCellNumber(16763.538d, "16.764"));
        }

        [Fact]
        public void ParseCellNumber_BinlikAyracliMetin_DogruOkunur()
        {
            // ESKİ HATA: "1.538" → Replace(",",".")+Invariant → 1,538. Artık 1538.
            Assert.Equal(1538m, ImportService.ParseCellNumber(null, "1.538"));
            Assert.Equal(1538.48m, ImportService.ParseCellNumber(null, "1.538,48"));   // tr
            Assert.Equal(1538.48m, ImportService.ParseCellNumber(null, "1,538.48"));   // invariant
        }

        [Fact]
        public void ParseCellNumber_OndalikVeKucukDegerler()
        {
            Assert.Equal(16763.538m, ImportService.ParseCellNumber(null, "16763,538"));
            Assert.Equal(16763.54m, ImportService.ParseCellNumber(null, "16.763,54"));
            Assert.Equal(538m, ImportService.ParseCellNumber(null, "538"));
            Assert.Equal(-1m, ImportService.ParseCellNumber(-1d, "-1"));   // iade (negatif)
        }

        [Fact]
        public void ParseCellNumber_BosVeGecersiz_NullDoner()
        {
            Assert.Null(ImportService.ParseCellNumber(null, null));
            Assert.Null(ImportService.ParseCellNumber(null, "   "));
            Assert.Null(ImportService.ParseCellNumber(null, "abc"));
        }

        // ── Normalize (kanonik eşleştirme anahtarı) ─────────────────────────

        [Fact]
        public void Normalize_TurkceNoktaSorununu_Birlestirir()
        {
            // i/İ/ı/I hepsi 'I' → kaynaklar arası eşleşir
            Assert.Equal(ImportService.Normalize("İzmir"), ImportService.Normalize("Izmir"));
            Assert.Equal(ImportService.Normalize("İTRİYAT"), ImportService.Normalize("itriyat"));
            Assert.Equal("DIYET", ImportService.Normalize("diyet"));
        }

        [Fact]
        public void Normalize_BoslukVeTrim()
        {
            Assert.Equal("IST KAGITHANE", ImportService.Normalize("  ist   kagithane "));
            Assert.Equal("", ImportService.Normalize(null));
            Assert.Equal("", ImportService.Normalize("   "));
        }

        [Fact]
        public void Normalize_DigerHarfleriKatlamaz()
        {
            // ş/ç/ğ/ü/ö korunur (yanlış birleşme olmasın) — yalnız büyük harf
            Assert.Equal("ŞIŞE ÇOĞU GÜÖ", ImportService.Normalize("şişe çoğu güö"));
        }

        // ── SuggestMatch (bulanık "bunu mu demek istediniz") ────────────────

        [Fact]
        public void SuggestMatch_YakinAday_Onerir()
        {
            var cands = new[] { "NICORETTE GUM", "NICORETTE PATCH", "VISINE" };
            Assert.Equal("NICORETTE GUM", ImportService.SuggestMatch("NICORETE GUM", cands));   // tek harf eksik
            Assert.Equal("VISINE", ImportService.SuggestMatch("Visine ", cands));               // boşluk/küçük harf
        }

        [Fact]
        public void SuggestMatch_AlakasizDeger_NullDoner()
        {
            var cands = new[] { "NICORETTE GUM", "VISINE" };
            Assert.Null(ImportService.SuggestMatch("KOMPRESÖR", cands));
            Assert.Null(ImportService.SuggestMatch("", cands));
        }

        [Fact]
        public void Levenshtein_TemelDurumlar()
        {
            Assert.Equal(0, ImportService.LevenshteinDistance("ABC", "ABC"));
            Assert.Equal(1, ImportService.LevenshteinDistance("ABC", "ABD"));
            Assert.Equal(3, ImportService.LevenshteinDistance("", "ABC"));
        }

        // ── AppendSuggestions (yapısal öneri + TargetId) ────────────────────

        [Fact]
        public void AppendSuggestions_YakinAday_TargetIdIleYapisalDoner()
        {
            var map = new Dictionary<string, int> { ["NICORETTE GUM"] = 10, ["VISINE"] = 20 };
            var sink = new List<MatchSuggestion>();
            ImportService.AppendSuggestions(sink, "Product", new[] { "NICORETE GUM" }, map);
            Assert.Single(sink);
            Assert.Equal("Product", sink[0].Type);
            Assert.Equal("NICORETE GUM", sink[0].Source);
            Assert.Equal("NICORETTE GUM", sink[0].Suggestion);
            Assert.Equal(10, sink[0].TargetId);
        }

        [Fact]
        public void AppendSuggestions_AlakasizDeger_OneriUretmez()
        {
            var map = new Dictionary<string, int> { ["VISINE"] = 20 };
            var sink = new List<MatchSuggestion>();
            ImportService.AppendSuggestions(sink, "Brick", new[] { "KOMPRESÖR XYZ" }, map);
            Assert.Empty(sink);
        }

        // ── BuildColumnMap (Sell-Out başlık-adına göre kolon eşleme) ─────────

        private static (string, System.Func<string, bool>)[] SellOutSpecs() => new (string, System.Func<string, bool>)[]
        {
            ("Ay",       h => h == "AY"),
            ("Brick",    h => h.Contains("BRICK")),
            ("GLN",      h => h.Contains("GLN")),
            ("Kurum",    h => h == "KURUM"),
            ("Marka",    h => h.Contains("MARKA")),
            ("Kutu",     h => h.Contains("KUTU")),
            ("AnaMalTL", h => h.Contains("ANA MAL")),
            ("MFTL",     h => h.Contains("MF")),
        };

        [Fact]
        public void BuildColumnMap_FazladanST_Kolonu_DogruKaydirir()
        {
            // 12 kolon: Brick ile Kurum GLN arasında ST var → Marka@9, KUTU@10
            using var ws = MakeSheet(2, "Yıl", "Ay", "İl", "İlçe", "Brick", "ST",
                "Kurum GLN", "Kurum", "Marka", "Toplam KUTU", "Ana Mal TL (Net İSF)", "MF TL (Net İSF)");
            var map = ImportService.BuildColumnMap(ws, 2, SellOutSpecs());
            Assert.Equal(2, map["Ay"]);
            Assert.Equal(5, map["Brick"]);
            Assert.Equal(7, map["GLN"]);
            Assert.Equal(8, map["Kurum"]);
            Assert.Equal(9, map["Marka"]);
            Assert.Equal(10, map["Kutu"]);
            Assert.Equal(11, map["AnaMalTL"]);
            Assert.Equal(12, map["MFTL"]);
        }

        [Fact]
        public void BuildColumnMap_KlasikOnBirKolon_DogruEsler()
        {
            using var ws = MakeSheet(2, "Yıl", "Ay", "İl", "İlçe", "Brick",
                "Kurum GLN", "Kurum", "Marka", "Toplam KUTU", "Ana Mal TL", "MF TL");
            var map = ImportService.BuildColumnMap(ws, 2, SellOutSpecs());
            Assert.Equal(6, map["GLN"]);
            Assert.Equal(8, map["Marka"]);
            Assert.Equal(9, map["Kutu"]);
        }

        [Fact]
        public void BuildColumnMap_EksikZorunlu_HaritadaYok()
        {
            using var ws = MakeSheet(2, "Ay", "Brick", "Toplam KUTU"); // Marka yok
            var map = ImportService.BuildColumnMap(ws, 2, SellOutSpecs());
            Assert.True(map.ContainsKey("Ay") && map.ContainsKey("Brick") && map.ContainsKey("Kutu"));
            Assert.False(map.ContainsKey("Marka"));
        }

        [Fact]
        public void BuildColumnMap_Hedef_UrunVeUrunGrubu_Karismaz()
        {
            // "Ürün" hedef ürün kolonu; "ÜrünGrubu" ayrı — Urun, GRUB içereni almamalı.
            var specs = new (string, System.Func<string, bool>)[]
            {
                ("Ay",        h => h == "AY"),
                ("Mumessil",  h => h.Contains("MÜMESS") || h.Contains("MUMESS") || h == "ST"),
                ("Brick",     h => h.Contains("BRICK")),
                ("UrunGrubu", h => h.Contains("GRUB")),
                ("Urun",      h => h.Contains("ÜRÜN") && !h.Contains("GRUB")),
                ("Hedef",     h => h.Contains("HEDEF")),
            };
            using var ws = MakeSheet(1, "Ay", "Mümessil", "Brick", "ÜrünGrubu", "Ürün", "HedefAdet");
            var map = ImportService.BuildColumnMap(ws, 1, specs);
            Assert.Equal(4, map["UrunGrubu"]);
            Assert.Equal(5, map["Urun"]);     // GRUB içeren kolon (4) DEĞİL
            Assert.Equal(2, map["Mumessil"]);
            Assert.Equal(6, map["Hedef"]);
        }

        [Fact]
        public void BuildColumnMap_Siparis_PersonelVeKurum()
        {
            var specs = new (string, System.Func<string, bool>)[]
            {
                ("Ay",       h => h == "AY"),
                ("Brick",    h => h.Contains("BRICK")),
                ("Personel", h => h.Contains("PERSONEL") || h.Contains("MÜMESS") || h == "ST"),
                ("Marka",    h => h.Contains("MARKA")),
                ("Kutu",     h => h.Contains("KUTU")),
                ("Kurum",    h => h.Contains("KURUM")),
            };
            using var ws = MakeSheet(2, "Yıl", "Ay", "Ana Depo", "Brick", "Personel", "Marka", "Toplam KUTU", "Ana Mal TL", "MF TL", "Kurum Adedi");
            var map = ImportService.BuildColumnMap(ws, 2, specs);
            Assert.Equal(5, map["Personel"]);
            Assert.Equal(6, map["Marka"]);
            Assert.Equal(7, map["Kutu"]);
            Assert.Equal(10, map["Kurum"]);
        }

        // ── Eczane Planı (UTT_Brick) kolon eşleme ───────────────────────────

        /// <summary>Kaynak dosyanın (UTT_Brick.xlsx → ECZANE) gerçek başlık satırı.</summary>
        private static ExcelWorksheet EczanePlaniSheet() => MakeSheet(1,
            "Ekip", "Bölge", "Çalışan ÜTT", "Brick", "Çalışan ÜTT", "Segment", "Uzmanlık",
            "Ünvan", "SMM ID", "SMM Adı Soyadı", "Kurum", "GLN", "Frek.\n", "Plan", "Ziyaret");

        [Fact]
        public void PharmacyPlanSpecs_GercekBaslikSatiri_DogruEsler()
        {
            using var ws = EczanePlaniSheet();
            var map = ImportService.BuildColumnMap(ws, 1, ImportService.PharmacyPlanSpecs());
            Assert.Equal(4, map["Brick"]);
            Assert.Equal(6, map["Segment"]);
            Assert.Equal(9, map["SmmId"]);
            Assert.Equal(11, map["Kurum"]);
            Assert.Equal(12, map["Gln"]);
            Assert.Equal(13, map["Frek"]);   // "Frek.\n" — Normalize satır sonunu kırpar
            Assert.Equal(14, map["Plan"]);
            Assert.Equal(15, map["Ziyaret"]);
        }

        [Fact]
        public void PharmacyPlanSpecs_CiftCalisanUttBasligi_IlkiniAlir()
        {
            // Kaynak dosyada "Çalışan ÜTT" İKİ KEZ geçiyor (kolon 3 ve 5); değerleri birebir aynı.
            // BuildColumnMap "ilk eşleşen kazanır" → kolon 3. Hangisi alınırsa alınsın sonuç aynı,
            // ama davranışın sabit kalması için pinlenir.
            using var ws = EczanePlaniSheet();
            var map = ImportService.BuildColumnMap(ws, 1, ImportService.PharmacyPlanSpecs());
            Assert.Equal(3, map["Utt"]);
        }

        [Fact]
        public void PharmacyPlanSpecs_UttEslesmesi_UzmanlikVeUnvaniKapmaz()
        {
            // "Uzmanlık" ve "Ünvan" da Ü ile başlıyor — Contains("ÜTT") yalnız ÜTT'yi tutmalı.
            using var ws = MakeSheet(1, "Uzmanlık", "Ünvan", "Çalışan ÜTT");
            var map = ImportService.BuildColumnMap(ws, 1, ImportService.PharmacyPlanSpecs());
            Assert.Equal(3, map["Utt"]);
        }

        [Fact]
        public void PharmacyPlanSpecs_PlanTamEslesme_KomsuKolonuKapmaz()
        {
            // "Planlanan Ziyaret" gibi bir kolon "Plan"ı kapmamalı (tam eşleşme kuralı).
            using var ws = MakeSheet(1, "Planlanan Ziyaret", "Brick", "GLN", "Plan");
            var map = ImportService.BuildColumnMap(ws, 1, ImportService.PharmacyPlanSpecs());
            Assert.Equal(4, map["Plan"]);
            // "Planlanan Ziyaret" ZIYARET içerdiği için Ziyaret'e düşer — beklenen, zararsız:
            // gerçek dosyada böyle bir kolon yok, "Ziyaret" kolonu tektir.
            Assert.Equal(1, map["Ziyaret"]);
        }

        [Fact]
        public void PharmacyPlanSpecs_SmmIdTamEslesme_AdiSoyadiniKapmaz()
        {
            using var ws = MakeSheet(1, "SMM Adı Soyadı", "SMM ID");
            var map = ImportService.BuildColumnMap(ws, 1, ImportService.PharmacyPlanSpecs());
            Assert.Equal(2, map["SmmId"]);
        }

        [Fact]
        public void PharmacyPlanSpecs_KolonSirasiDegisse_AdaGoreBulur()
        {
            // Sabit konum yerine ad eşlemesi: kolonlar karışsa/araya girse de doğru bulunmalı.
            using var ws = MakeSheet(1, "GLN", "Ziyaret", "Ekstra", "Plan", "Brick", "Çalışan ÜTT");
            var map = ImportService.BuildColumnMap(ws, 1, ImportService.PharmacyPlanSpecs());
            Assert.Equal(1, map["Gln"]);
            Assert.Equal(2, map["Ziyaret"]);
            Assert.Equal(4, map["Plan"]);
            Assert.Equal(5, map["Brick"]);
            Assert.Equal(6, map["Utt"]);
        }

        [Fact]
        public void PharmacyPlanSpecs_YanlisDosya_ZorunluKolonlarBulunamaz()
        {
            // Sell-Out dosyası bu panele yüklenirse: Plan/Ziyaret/Frek yok → import reddetmeli.
            using var ws = MakeSheet(1, "Yıl", "Ay", "İl", "İlçe", "Brick",
                "Kurum GLN", "Kurum", "Marka", "Toplam KUTU", "Ana Mal TL", "MF TL");
            var map = ImportService.BuildColumnMap(ws, 1, ImportService.PharmacyPlanSpecs());
            Assert.False(map.ContainsKey("Plan"));
            Assert.False(map.ContainsKey("Ziyaret"));
            Assert.False(map.ContainsKey("Frek"));
            // Brick ve GLN eşleşir ama zorunlu üçlüden Plan eksik → ImportPharmacyPlanAsync hata verir.
            Assert.True(map.ContainsKey("Brick"));
            Assert.True(map.ContainsKey("Gln"));
        }
    }
}
