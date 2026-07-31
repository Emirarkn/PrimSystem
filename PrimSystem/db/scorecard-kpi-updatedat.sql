/* ============================================================================
   PrimSystem — ScorecardWeights.UpdatedAt + KPICriteria.UpdatedAt
   ----------------------------------------------------------------------------
   "Geriye dönük (tümünü güncelle)" düzenleme satırı YERİNDE günceller; yeni satır
   eklenmediği için CreatedAt değişmez ve "⟳ Yeniden Hesapla gerekli" tazelik bandı
   sessiz kalırdı. UpdatedAt bu boşluğu kapatır (GetCalcFreshnessAsync okur).

   NOT: Program.cs başlangıç self-heal'i bu kolonları zaten idempotent olarak ekler;
   bu script yalnız yedek/elle çalıştırma içindir. Idempotent — tekrar çalıştırılabilir.
   SSMS'te PrimDB üzerinde Execute.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;   -- filtered index'ler nedeniyle ALTER TABLE bunu gerektirir
SET ANSI_NULLS ON;
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.ScorecardWeights') AND name = 'UpdatedAt')
BEGIN
    ALTER TABLE dbo.ScorecardWeights ADD UpdatedAt DATETIME2 NULL;
    PRINT 'ScorecardWeights.UpdatedAt eklendi.';
END
ELSE PRINT 'ScorecardWeights.UpdatedAt zaten var.';

IF NOT EXISTS (SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.KPICriteria') AND name = 'UpdatedAt')
BEGIN
    ALTER TABLE dbo.KPICriteria ADD UpdatedAt DATETIME2 NULL;
    PRINT 'KPICriteria.UpdatedAt eklendi.';
END
ELSE PRINT 'KPICriteria.UpdatedAt zaten var.';

/* Doğrulama */
SELECT t.name AS TabloAdi, c.name AS KolonAdi
FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
WHERE c.name = 'UpdatedAt' AND t.name IN ('ScorecardWeights', 'KPICriteria');
