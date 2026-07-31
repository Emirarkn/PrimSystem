/* ============================================================================
   PrimSystem — Aveeno (P1) → DİĞER İTRİYAT (JB GRUBU / NT GRUBU) (P13) birleştirmesi
   ----------------------------------------------------------------------------
   SORUN: Aveeno artık ayrı bir prim kalemi değil; DİĞER İTRİYAT'a dahil. Config
   tarafı yapılmıştı (P1 IsActive=0, P13 alias'ına Aveeno eklendi, 2026-04-01
   ağırlık revizyonunda Aveeno 5→0 / P13 3→5), ama VERİ taşınmamıştı.

   CalculationService ağırlıkları `w.Product.IsPrimEligible && w.Product.IsActive`
   ile filtreliyor (CalculationService.cs:317-319). P1 pasif olduğu için altındaki
   hedef/çıkış satırları skorlamaya HİÇ girmiyor → hacim tamamen kayboluyor.
   Q2 2026'da kaybolan: 3.470,00 hedef + 1.306 kutu çıkış.

   Sebep: hedef importu 2026-06-21, sell-out importu 2026-07-23 — ikisi de P1
   pasifleştirilmeden ÖNCE çalışmış. Sonraki import'lar sorunsuz: P1 pasif olduğu
   için BuildProductMap onu listeye almıyor (ImportService.cs:1303-1306) ve
   'AVEENO' alias'ı doğrudan P13'e düşüyor.

   ÇÖZÜM: Kapsam içindeki P1 satırlarını P13'e katla. Anahtarlar import upsert
   anahtarlarıyla BİREBİR aynı — yoksa sonraki import'lar dedupe edemez:
     Targets         (UserId, BrickId, PeriodId)   ImportService.cs:1315-1317
     SellOuts        (BrickId, PeriodId)           ImportService.cs:754-756
     Orders          (BrickId, PeriodId)           ImportService.cs:1078
     SellOutDetails  — anahtar yok, düz re-point (aşağıda gerekçesi)

   KAPSAM: YALNIZ 2026-04 ve sonrası. Ağırlık versiyon sınırı da tam burada
   (ValidFrom = 2026-04-01). Geçmiş çeyreklerde (2024, 2025, Q1 2026) Aveeno kendi
   ağırlığıyla (5) skorlanıp YAYINLANDI — o veriye DOKUNULMAZ.

   Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -i aveeno-merge.sql
            ya da SSMS'te PrimDB üzerinde açıp Execute (dosyayı UTF-8 aç).
   Idempotent — tekrar çalıştırmak güvenlidir (3. adım kapsamdaki P1 satırlarını
   sildiği için ikinci çalıştırma tam no-op olur).

   SONRAKİ ADIM: /calculate ekranından 2026 Q2 yeniden hesaplanmalı.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Src int = 1;    -- Aveeno (pasif)
DECLARE @Dst int = 13;   -- DİĞER İTRİYAT (JB GRUBU / NT GRUBU)

/* Kapsam: 2026-04 ve sonrası tüm dönemler */
DECLARE @Scope TABLE (PeriodId int PRIMARY KEY);
INSERT INTO @Scope (PeriodId)
SELECT PeriodId FROM dbo.Periods
WHERE Year > 2026 OR (Year = 2026 AND Month >= 4);

/* ------------------------------------------------------------------ */
/* 0) ÖNCE — mevcut durum                                              */
/* ------------------------------------------------------------------ */
PRINT '=== ÖNCE ===';

SELECT 'Products' AS Tablo, ProductId, IsActive, IsPrimEligible, Name, ImportAlias
FROM dbo.Products WHERE ProductId IN (@Src, @Dst) ORDER BY ProductId;

SELECT 'Kapsam içi' AS Kapsam, Tablo, ProductId, Satir, Miktar FROM (
    SELECT 'Targets'        AS Tablo, ProductId, COUNT(*) AS Satir, SUM(TargetQty) AS Miktar
        FROM dbo.Targets        WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
    UNION ALL
    SELECT 'SellOuts',        ProductId, COUNT(*), SUM(Quantity)
        FROM dbo.SellOuts       WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
    UNION ALL
    SELECT 'SellOutDetails',  ProductId, COUNT(*), SUM(Quantity)
        FROM dbo.SellOutDetails WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
    UNION ALL
    SELECT 'Orders',          ProductId, COUNT(*), SUM(BoxQuantity)
        FROM dbo.Orders         WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
) x ORDER BY Tablo, ProductId;

/* Kapsam DIŞI (geçmiş) — bu satırlara dokunulmayacak; sonra aynı kalmalı */
SELECT 'Kapsam DIŞI (dokunulmaz)' AS Kapsam, Tablo, ProductId, Satir, Miktar FROM (
    SELECT 'Targets'  AS Tablo, ProductId, COUNT(*) AS Satir, SUM(TargetQty) AS Miktar
        FROM dbo.Targets  WHERE ProductId = @Src AND PeriodId NOT IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
    UNION ALL
    SELECT 'SellOuts', ProductId, COUNT(*), SUM(Quantity)
        FROM dbo.SellOuts WHERE ProductId = @Src AND PeriodId NOT IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
) x ORDER BY Tablo;

/* ------------------------------------------------------------------ */
/* GUARD — tekrar eden anahtar varsa DUR                               */
/* Toplama adımı (A) 1:1 join varsayar. Kaynak ya da hedefte aynı       */
/* anahtardan iki satır varsa miktar mükerrer eklenir → sessiz bozulma. */
/* ------------------------------------------------------------------ */
DECLARE @dupT int, @dupS int, @dupO int;

SELECT @dupT = COUNT(*) FROM (
    SELECT 1 AS n FROM dbo.Targets WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope)
    GROUP BY ProductId, UserId, BrickId, PeriodId HAVING COUNT(*) > 1) d;

SELECT @dupS = COUNT(*) FROM (
    SELECT 1 AS n FROM dbo.SellOuts WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope)
    GROUP BY ProductId, BrickId, PeriodId HAVING COUNT(*) > 1) d;

SELECT @dupO = COUNT(*) FROM (
    SELECT 1 AS n FROM dbo.Orders WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope)
    GROUP BY ProductId, BrickId, PeriodId HAVING COUNT(*) > 1) d;

IF (@dupT > 0 OR @dupS > 0 OR @dupO > 0)
BEGIN
    DECLARE @msg nvarchar(400) = CONCAT(
        N'DURDURULDU — kapsamda tekrar eden anahtar var. Targets:', @dupT,
        N' SellOuts:', @dupS, N' Orders:', @dupO,
        N'. Toplama adımı mükerrer sayar; önce bu satırlar temizlenmeli.');
    RAISERROR(@msg, 16, 1);
    RETURN;
END

PRINT 'Guard OK — kapsamda tekrar eden anahtar yok.';

/* ------------------------------------------------------------------ */
/* YEDEK — geri dönüş için kapsam içi P1 satırlarının fotoğrafı        */
/* Yalnız ilk çalıştırmada oluşur (IF NOT EXISTS); tekrar çalıştırmada  */
/* orijinal yedek KORUNUR, üzerine yazılmaz.                           */
/* Geri alma gerekirse: bu tablolardaki satırlar ProductId=1 ile geri   */
/* insert edilir, ardından P13'ten aynı miktarlar düşülür.             */
/* Temizlik (birkaç çeyrek sonra): DROP TABLE dbo._bak_aveeno_*;       */
/* ------------------------------------------------------------------ */
IF OBJECT_ID('dbo._bak_aveeno_Targets') IS NULL
    SELECT * INTO dbo._bak_aveeno_Targets FROM dbo.Targets
    WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope);

IF OBJECT_ID('dbo._bak_aveeno_SellOuts') IS NULL
    SELECT * INTO dbo._bak_aveeno_SellOuts FROM dbo.SellOuts
    WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope);

IF OBJECT_ID('dbo._bak_aveeno_SellOutDetails') IS NULL
    SELECT * INTO dbo._bak_aveeno_SellOutDetails FROM dbo.SellOutDetails
    WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope);

IF OBJECT_ID('dbo._bak_aveeno_Orders') IS NULL
    SELECT * INTO dbo._bak_aveeno_Orders FROM dbo.Orders
    WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope);

/* Hedef tarafın da fotoğrafı — toplama sonrası karşılaştırma için */
IF OBJECT_ID('dbo._bak_aveeno_P13_Targets') IS NULL
    SELECT * INTO dbo._bak_aveeno_P13_Targets FROM dbo.Targets
    WHERE ProductId = @Dst AND PeriodId IN (SELECT PeriodId FROM @Scope);

IF OBJECT_ID('dbo._bak_aveeno_P13_SellOuts') IS NULL
    SELECT * INTO dbo._bak_aveeno_P13_SellOuts FROM dbo.SellOuts
    WHERE ProductId = @Dst AND PeriodId IN (SELECT PeriodId FROM @Scope);

SELECT 'Yedek' AS Bilgi,
    (SELECT COUNT(*) FROM dbo._bak_aveeno_Targets)        AS P1_Targets,
    (SELECT COUNT(*) FROM dbo._bak_aveeno_SellOuts)       AS P1_SellOuts,
    (SELECT COUNT(*) FROM dbo._bak_aveeno_SellOutDetails) AS P1_SellOutDetails,
    (SELECT COUNT(*) FROM dbo._bak_aveeno_Orders)         AS P1_Orders,
    (SELECT COUNT(*) FROM dbo._bak_aveeno_P13_Targets)    AS P13_Targets,
    (SELECT COUNT(*) FROM dbo._bak_aveeno_P13_SellOuts)   AS P13_SellOuts;

/* ------------------------------------------------------------------ */
/* BİRLEŞTİRME                                                         */
/* ------------------------------------------------------------------ */
BEGIN TRANSACTION;

DECLARE @a int, @b int, @c int;

/* ---- Targets: (UserId, BrickId, PeriodId) ---- */
/* A) P13 satırı VARSA → P1 miktarını üstüne ekle */
UPDATE tgt
SET tgt.TargetQty  = tgt.TargetQty + src.Qty,
    tgt.ImportedAt = CASE WHEN src.MaxImp > tgt.ImportedAt THEN src.MaxImp ELSE tgt.ImportedAt END
FROM dbo.Targets tgt
INNER JOIN (
    SELECT UserId, BrickId, PeriodId, SUM(TargetQty) AS Qty, MAX(ImportedAt) AS MaxImp
    FROM dbo.Targets
    WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope)
    GROUP BY UserId, BrickId, PeriodId
) src ON src.UserId = tgt.UserId AND src.BrickId = tgt.BrickId AND src.PeriodId = tgt.PeriodId
WHERE tgt.ProductId = @Dst AND tgt.PeriodId IN (SELECT PeriodId FROM @Scope);
SET @a = @@ROWCOUNT;

/* B) P13 satırı YOKSA → P1 satırını P13'e taşı */
UPDATE t SET t.ProductId = @Dst
FROM dbo.Targets t
WHERE t.ProductId = @Src AND t.PeriodId IN (SELECT PeriodId FROM @Scope)
  AND NOT EXISTS (SELECT 1 FROM dbo.Targets d
                  WHERE d.ProductId = @Dst AND d.UserId = t.UserId
                    AND d.BrickId = t.BrickId AND d.PeriodId = t.PeriodId);
SET @b = @@ROWCOUNT;

/* C) Kapsamda kalan (A'da tüketilmiş) P1 satırlarını sil */
DELETE FROM dbo.Targets WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope);
SET @c = @@ROWCOUNT;
PRINT CONCAT('Targets        → eklendi:', @a, ' taşındı:', @b, ' silindi:', @c);

/* ---- SellOuts: (BrickId, PeriodId) ---- */
/* AmountTL/MFTL nullable → ISNULL ile toplanır, aksi halde NULL yutar.        */
/* UserId hedef satırdakiyle bırakılır (kapsamda UserId çakışması yok — teyitli) */
UPDATE so
SET so.Quantity     = so.Quantity + src.Qty,
    so.AmountTL     = ISNULL(so.AmountTL, 0) + src.Amt,
    so.MFTL         = ISNULL(so.MFTL, 0)     + src.Mf,
    so.SnapshotDate = CASE WHEN src.MaxSnap > so.SnapshotDate THEN src.MaxSnap ELSE so.SnapshotDate END,
    so.ImportedAt   = CASE WHEN src.MaxImp  > so.ImportedAt   THEN src.MaxImp  ELSE so.ImportedAt   END
FROM dbo.SellOuts so
INNER JOIN (
    SELECT BrickId, PeriodId,
           SUM(Quantity)          AS Qty,
           SUM(ISNULL(AmountTL,0)) AS Amt,
           SUM(ISNULL(MFTL,0))     AS Mf,
           MAX(SnapshotDate)       AS MaxSnap,
           MAX(ImportedAt)         AS MaxImp
    FROM dbo.SellOuts
    WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope)
    GROUP BY BrickId, PeriodId
) src ON src.BrickId = so.BrickId AND src.PeriodId = so.PeriodId
WHERE so.ProductId = @Dst AND so.PeriodId IN (SELECT PeriodId FROM @Scope);
SET @a = @@ROWCOUNT;

UPDATE s SET s.ProductId = @Dst
FROM dbo.SellOuts s
WHERE s.ProductId = @Src AND s.PeriodId IN (SELECT PeriodId FROM @Scope)
  AND NOT EXISTS (SELECT 1 FROM dbo.SellOuts d
                  WHERE d.ProductId = @Dst AND d.BrickId = s.BrickId AND d.PeriodId = s.PeriodId);
SET @b = @@ROWCOUNT;

DELETE FROM dbo.SellOuts WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope);
SET @c = @@ROWCOUNT;
PRINT CONCAT('SellOuts       → eklendi:', @a, ' taşındı:', @b, ' silindi:', @c);

/* ---- Orders: (BrickId, PeriodId) ---- */
/* InstitutionCount DISTINCT kurum sayısı — toplamak, iki markayı da sipariş eden */
/* kurumu iki kez sayar. MAX alt sınır verir; şişirmek yerine muhafazakâr kalınır. */
/* Not: prim matematiğine girmez — KPI4 (Strike Rate) Orders.GLN üzerinden        */
/* çalışıyor (CalculationService.cs:496), InstitutionCount yalnız raporlama.      */
UPDATE o
SET o.BoxQuantity      = o.BoxQuantity + src.Qty,
    o.AmountTL         = ISNULL(o.AmountTL, 0) + src.Amt,
    o.MFTL             = ISNULL(o.MFTL, 0)     + src.Mf,
    o.InstitutionCount = CASE WHEN src.MaxInst > ISNULL(o.InstitutionCount, 0)
                              THEN src.MaxInst ELSE o.InstitutionCount END,
    o.SnapshotDate     = CASE WHEN src.MaxSnap > o.SnapshotDate THEN src.MaxSnap ELSE o.SnapshotDate END,
    o.ImportedAt       = CASE WHEN src.MaxImp  > o.ImportedAt   THEN src.MaxImp  ELSE o.ImportedAt   END
FROM dbo.Orders o
INNER JOIN (
    SELECT BrickId, PeriodId,
           SUM(BoxQuantity)         AS Qty,
           SUM(ISNULL(AmountTL,0))  AS Amt,
           SUM(ISNULL(MFTL,0))      AS Mf,
           MAX(InstitutionCount)    AS MaxInst,
           MAX(SnapshotDate)        AS MaxSnap,
           MAX(ImportedAt)          AS MaxImp
    FROM dbo.Orders
    WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope)
    GROUP BY BrickId, PeriodId
) src ON src.BrickId = o.BrickId AND src.PeriodId = o.PeriodId
WHERE o.ProductId = @Dst AND o.PeriodId IN (SELECT PeriodId FROM @Scope);
SET @a = @@ROWCOUNT;

UPDATE o SET o.ProductId = @Dst
FROM dbo.Orders o
WHERE o.ProductId = @Src AND o.PeriodId IN (SELECT PeriodId FROM @Scope)
  AND NOT EXISTS (SELECT 1 FROM dbo.Orders d
                  WHERE d.ProductId = @Dst AND d.BrickId = o.BrickId AND d.PeriodId = o.PeriodId);
SET @b = @@ROWCOUNT;

DELETE FROM dbo.Orders WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope);
SET @c = @@ROWCOUNT;
PRINT CONCAT('Orders         → eklendi:', @a, ' taşındı:', @b, ' silindi:', @c);

/* ---- SellOutDetails: TOPLAMA YOK, düz re-point ---- */
/* Bu tablo GLN kırılımındaki HAM satırları tutuyor; import dedupe yapmadan     */
/* doğrudan insert ediyor (ImportService.cs:864+), yani tekrar eden anahtar     */
/* zaten normal. Korunması gereken tek invariant Entities.cs:188-190'da yazılı: */
/* detayların (PeriodId, BrickId, ProductId) toplamı = SellOuts.Quantity.       */
/* Düz re-point bunu korur; toplama gereksiz yere ham veriyi ezerdi.            */
UPDATE dbo.SellOutDetails SET ProductId = @Dst
WHERE ProductId = @Src AND PeriodId IN (SELECT PeriodId FROM @Scope);
SET @a = @@ROWCOUNT;
PRINT CONCAT('SellOutDetails → taşındı:', @a);

/* ---- P13 alias'ı: AVEENO garantisi + büyük harf normalizasyonu ---- */
/* Davranış değişmez (Normalize zaten büyük harfe çeviriyor), diğer alias'larla */
/* görsel tutarlılık için. Alias hiç yoksa eklenir — script tek başına yeterli.  */
UPDATE dbo.Products
SET ImportAlias = CASE WHEN ImportAlias IS NULL OR LTRIM(RTRIM(ImportAlias)) = N''
                       THEN N'AVEENO' ELSE ImportAlias + N';AVEENO' END
WHERE ProductId = @Dst
  AND (ImportAlias IS NULL OR N';' + ImportAlias + N';' NOT LIKE N'%;AVEENO;%');
SET @a = @@ROWCOUNT;

UPDATE dbo.Products
SET ImportAlias = REPLACE(ImportAlias COLLATE Latin1_General_CS_AS, N'Aveeno', N'AVEENO')
WHERE ProductId = @Dst
  AND ImportAlias COLLATE Latin1_General_CS_AS LIKE N'%Aveeno%';
SET @b = @@ROWCOUNT;
PRINT CONCAT('P13 alias      → eklendi:', @a, ' normalize:', @b);

COMMIT TRANSACTION;

/* ------------------------------------------------------------------ */
/* SONRA — doğrulama                                                   */
/* ------------------------------------------------------------------ */
PRINT '=== SONRA ===';

SELECT 'Products' AS Tablo, ProductId, IsActive, IsPrimEligible, Name, ImportAlias
FROM dbo.Products WHERE ProductId IN (@Src, @Dst) ORDER BY ProductId;

/* Toplamlar DEĞİŞMEMELİ, yalnız satır sayısı düşmeli */
SELECT 'Kapsam içi' AS Kapsam, Tablo, ProductId, Satir, Miktar FROM (
    SELECT 'Targets'        AS Tablo, ProductId, COUNT(*) AS Satir, SUM(TargetQty) AS Miktar
        FROM dbo.Targets        WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
    UNION ALL
    SELECT 'SellOuts',        ProductId, COUNT(*), SUM(Quantity)
        FROM dbo.SellOuts       WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
    UNION ALL
    SELECT 'SellOutDetails',  ProductId, COUNT(*), SUM(Quantity)
        FROM dbo.SellOutDetails WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
    UNION ALL
    SELECT 'Orders',          ProductId, COUNT(*), SUM(BoxQuantity)
        FROM dbo.Orders         WHERE ProductId IN (@Src,@Dst) AND PeriodId IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
) x ORDER BY Tablo, ProductId;

/* Kapsamda P1 kalmamalı — dördü de 0 */
SELECT 'Kapsamda kalan P1 (0 olmalı)' AS Kontrol,
    (SELECT COUNT(*) FROM dbo.Targets        WHERE ProductId=@Src AND PeriodId IN (SELECT PeriodId FROM @Scope)) AS Targets,
    (SELECT COUNT(*) FROM dbo.SellOuts       WHERE ProductId=@Src AND PeriodId IN (SELECT PeriodId FROM @Scope)) AS SellOuts,
    (SELECT COUNT(*) FROM dbo.SellOutDetails WHERE ProductId=@Src AND PeriodId IN (SELECT PeriodId FROM @Scope)) AS SellOutDetails,
    (SELECT COUNT(*) FROM dbo.Orders         WHERE ProductId=@Src AND PeriodId IN (SELECT PeriodId FROM @Scope)) AS Orders;

/* Geçmiş dokunulmadı mı — ÖNCE bölümündeki aynı sorgu, aynı sonucu vermeli */
SELECT 'Kapsam DIŞI (aynı kalmalı)' AS Kapsam, Tablo, ProductId, Satir, Miktar FROM (
    SELECT 'Targets'  AS Tablo, ProductId, COUNT(*) AS Satir, SUM(TargetQty) AS Miktar
        FROM dbo.Targets  WHERE ProductId = @Src AND PeriodId NOT IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
    UNION ALL
    SELECT 'SellOuts', ProductId, COUNT(*), SUM(Quantity)
        FROM dbo.SellOuts WHERE ProductId = @Src AND PeriodId NOT IN (SELECT PeriodId FROM @Scope) GROUP BY ProductId
) x ORDER BY Tablo;

PRINT 'Birleştirme tamam. SONRAKİ ADIM: /calculate ekranından 2026 Q2''yi yeniden hesapla + yayınla.';
