/* ============================================================================
   PrimSystem — UserBricks.SharePercent (Ortak Brick payı)
   ----------------------------------------------------------------------------
   Bir brick iki ST'ye ortak atandığında her sahibin pay yüzdesi. Tek sahip = 100.
   Atıf (hedef/sell-out/sipariş) bu payla çarpılır → 100 iken davranış değişmez.
   Idempotent: kolon yoksa ekler, mevcut satırları 100'e sabitler.
   SSMS'te PrimDB üzerinde Execute. (Yeni derlemeyi çalıştırmadan ÖNCE önerilir.)
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;  -- filtered index'ler için ALTER TABLE bunu gerektirir
SET ANSI_NULLS ON;
SET NOCOUNT ON;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.UserBricks') AND name = 'SharePercent')
BEGIN
    ALTER TABLE dbo.UserBricks
        ADD SharePercent DECIMAL(5,2) NOT NULL CONSTRAINT DF_UserBricks_SharePercent DEFAULT (100);
    PRINT 'SharePercent kolonu eklendi (DEFAULT 100).';
END
ELSE
    PRINT 'SharePercent kolonu zaten var.';

/* Güvenlik: NULL/0 kalmış satırları 100 yap (tek sahip varsayımı) */
UPDATE dbo.UserBricks SET SharePercent = 100
WHERE SharePercent IS NULL OR SharePercent = 0;
PRINT 'Mevcut satirlar 100 paya sabitlendi (gerekliyse).';
