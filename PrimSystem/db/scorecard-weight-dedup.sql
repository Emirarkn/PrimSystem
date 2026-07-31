/* ============================================================================
   PrimSystem — ScorecardWeights çift AKTİF satır temizliği (dedup)
   ----------------------------------------------------------------------------
   Her üründe birden çok ValidTo=NULL (aktif) satır birikmiş (P14 kalıntısı;
   eski SaveEdit yalnız BİR aktifi kapatıyordu). Hesaplama artık deterministik
   seçtiği için (max ValidFrom, sonra max WeightId) değer değişmez; bu script
   yalnızca FAZLA aktif satırları kapatır → her üründe TEK aktif satır kalır.
   MUHAFAZAKÂR: hiçbir satır SİLİNMEZ, yalnız ValidTo doldurulur. Idempotent.
   SSMS'te PrimDB üzerinde Execute.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRAN;

;WITH ranked AS (
    SELECT w.WeightId, w.ProductId, w.ValidFrom,
        ROW_NUMBER() OVER (
            PARTITION BY w.ProductId
            ORDER BY w.ValidFrom DESC, w.WeightId DESC) AS rn
    FROM dbo.ScorecardWeights w
    WHERE w.ValidTo IS NULL
)
UPDATE w
SET w.ValidTo = w.ValidFrom          -- fazla aktif duplike → kapat (geçerli olan rn=1 korunur)
FROM dbo.ScorecardWeights w
JOIN ranked r ON r.WeightId = w.WeightId
WHERE r.rn > 1;

DECLARE @closed INT = @@ROWCOUNT;

/* Doğrulama: her üründe tek aktif satır kalmalı */
IF EXISTS (
    SELECT w.ProductId FROM dbo.ScorecardWeights w
    WHERE w.ValidTo IS NULL
    GROUP BY w.ProductId HAVING COUNT(*) > 1)
BEGIN
    RAISERROR('Hala coklu aktif satir var — geri alindi.', 16, 1);
    ROLLBACK TRAN; RETURN;
END

COMMIT TRAN;
PRINT CONCAT('Dedup tamam. Kapatilan fazla aktif satir: ', @closed);

/* Sonuç: ürün başına aktif satır sayısı (hepsi 1 olmalı) */
SELECT w.ProductId, p.Name, COUNT(*) AS AktifSatir
FROM dbo.ScorecardWeights w JOIN dbo.Products p ON p.ProductId = w.ProductId
WHERE w.ValidTo IS NULL
GROUP BY w.ProductId, p.Name
ORDER BY p.Name;
