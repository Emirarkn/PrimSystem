-- Institutions (Eczane master): GLN-merkezli eczane → brick organizasyonu.
-- Tablo baseline'da zaten VAR; bu script yalnız yeni kolonları + GLN unique index ekler. Idempotent.
-- Çalıştırıldıktan sonra sell-out importu Institutions'ı GLN bazında doldurur/günceller.

-- GLN nvarchar(max) ise index'lenemez → indekslenebilir tipe çevir (GLN'ler 13 hane; kayıp yok).
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.Institutions') AND name = 'GLN' AND max_length = -1)
    ALTER TABLE dbo.Institutions ALTER COLUMN GLN NVARCHAR(450) NULL;
GO

IF COL_LENGTH('dbo.Institutions', 'Ilce') IS NULL
    ALTER TABLE dbo.Institutions ADD Ilce NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH('dbo.Institutions', 'BrickOverride') IS NULL
    ALTER TABLE dbo.Institutions ADD BrickOverride BIT NOT NULL
        CONSTRAINT DF_Institutions_BrickOverride DEFAULT (0);
GO

IF COL_LENGTH('dbo.Institutions', 'LastSeenAt') IS NULL
    ALTER TABLE dbo.Institutions ADD LastSeenAt DATETIME2 NULL;
GO

-- EF migration ile aynı ad (IX_Institutions_GLN) — çift oluşturmayı önler.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_Institutions_GLN' AND object_id = OBJECT_ID('dbo.Institutions'))
    CREATE UNIQUE INDEX IX_Institutions_GLN ON dbo.Institutions(GLN) WHERE GLN IS NOT NULL;
GO
