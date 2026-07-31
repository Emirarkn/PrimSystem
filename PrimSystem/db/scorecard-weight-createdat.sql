/* ============================================================================
   PrimSystem — ScorecardWeights.CreatedAt (ağırlık değişikliği tazeliği)
   ----------------------------------------------------------------------------
   Freshness bandı "ürün ağırlıkları son hesaplamadan sonra değişti → Yeniden
   Hesapla" uyarısını verebilsin diye ağırlık kaydının oluşturulma zamanı.
   Idempotent. SSMS'te PrimDB üzerinde Execute. (Program.cs self-heal de ekler.)
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.ScorecardWeights') AND name = 'CreatedAt')
BEGIN
    ALTER TABLE dbo.ScorecardWeights
        ADD CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_ScorecardWeights_CreatedAt DEFAULT (SYSUTCDATETIME());
    PRINT 'CreatedAt kolonu eklendi (DEFAULT SYSUTCDATETIME).';
END
ELSE
    PRINT 'CreatedAt kolonu zaten var.';
