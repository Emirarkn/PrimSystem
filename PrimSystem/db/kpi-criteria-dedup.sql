/* ============================================================================
   PrimSystem — KPICriteria çift AKTİF satır temizliği (dedup)
   ----------------------------------------------------------------------------
   Her (Year, Name) için birden çok ValidTo=NULL (aktif) satır birikmiş; canlıda
   4 KPI yerine yıl başına 8 satır vardı. Hesaplama artık deterministik seçiyor
   (CalculationService.PickKpi → max CriteriaId) ve Scorecard aynı ad+yıl için
   TÜM kopyaları güncelliyor, yani değer değişmez; bu script yalnızca FAZLA aktif
   satırları kapatır → her (Year, Name) için TEK aktif satır kalır.
   MUHAFAZAKÂR: hiçbir satır SİLİNMEZ, yalnız ValidTo doldurulur. Idempotent.
   SSMS'te PrimDB üzerinde Execute.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRAN;

;WITH ranked AS (
    SELECT k.CriteriaId, k.ValidFrom,
        ROW_NUMBER() OVER (
            PARTITION BY k.Year, k.Name
            ORDER BY k.CriteriaId DESC) AS rn      -- en son oluşturulan geçerli sayılır
    FROM dbo.KPICriteria k
    WHERE k.ValidTo IS NULL
)
UPDATE k
SET k.ValidTo = k.ValidFrom          -- fazla aktif duplike → kapat (rn=1 korunur)
FROM dbo.KPICriteria k
JOIN ranked r ON r.CriteriaId = k.CriteriaId
WHERE r.rn > 1;

DECLARE @closed INT = @@ROWCOUNT;

/* Doğrulama: her (Year, Name) için tek aktif satır kalmalı */
IF EXISTS (
    SELECT 1 FROM dbo.KPICriteria
    WHERE ValidTo IS NULL
    GROUP BY Year, Name HAVING COUNT(*) > 1)
BEGIN
    RAISERROR('Hala coklu aktif KPI satiri var — geri alindi.', 16, 1);
    ROLLBACK TRAN; RETURN;
END

COMMIT TRAN;
PRINT CONCAT('KPI dedup tamam. Kapatilan fazla aktif satir: ', @closed);

/* Sonuç: (yıl, KPI) başına aktif satır sayısı (hepsi 1 olmalı) */
SELECT Year, Name, COUNT(*) AS AktifSatir, MAX(Weight) AS Agirlik
FROM dbo.KPICriteria
WHERE ValidTo IS NULL
GROUP BY Year, Name
ORDER BY Year, Name;
