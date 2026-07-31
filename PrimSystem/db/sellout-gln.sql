/* ============================================================================
   PrimSystem — Sell-Out GLN/Kurum Detay Tablosu Şema Değişikliği
   ----------------------------------------------------------------------------
   ÖNEMLİ: Yeni sürüm bu DDL uygulanmadan ÇALIŞMAZ — Sell-Out import'u her geçerli
   satırın GLN (kurum) kırılımını SellOutDetails tablosuna yazar. SellOuts tablosu
   brick+marka+dönem TOPLAMINI tutar (skor için); bu tablo kurum bazlı ham detayı tutar.

   Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -i sellout-gln.sql
            ya da SSMS'te PrimDB üzerinde açıp Execute.

   Tümü IDEMPOTENT'tir (varsa atlanır).
   ============================================================================ */

SET NOCOUNT ON;

/* 1) SellOutDetails — kurum (GLN) bazlı sell-out detayı */
IF OBJECT_ID('dbo.SellOutDetails','U') IS NULL
BEGIN
    CREATE TABLE dbo.SellOutDetails (
        SellOutDetailId BIGINT IDENTITY(1,1) PRIMARY KEY,
        BrickId         INT NOT NULL,
        ProductId       INT NOT NULL,
        PeriodId        INT NOT NULL,
        GLN             NVARCHAR(450) NOT NULL,
        KurumName       NVARCHAR(400) NULL,
        Il              NVARCHAR(200) NULL,
        Ilce            NVARCHAR(200) NULL,
        Quantity        DECIMAL(18,4) NOT NULL DEFAULT(0),
        AmountTL        DECIMAL(18,4) NULL,
        MFTL            DECIMAL(18,4) NULL,
        UserId          INT NULL,
        ImportedAt      DATETIME2 NOT NULL CONSTRAINT DF_SellOutDetails_ImportedAt DEFAULT(SYSUTCDATETIME())
    );
END

/* 2) Indeksler — eşleşme/teyit ve detay sorguları için (idempotent) */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SellOutDetails_Period_Brick_Product' AND object_id=OBJECT_ID('dbo.SellOutDetails'))
    CREATE INDEX IX_SellOutDetails_Period_Brick_Product ON dbo.SellOutDetails(PeriodId, BrickId, ProductId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SellOutDetails_GLN' AND object_id=OBJECT_ID('dbo.SellOutDetails'))
    CREATE INDEX IX_SellOutDetails_GLN ON dbo.SellOutDetails(GLN);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SellOutDetails_ImportedAt' AND object_id=OBJECT_ID('dbo.SellOutDetails'))
    CREATE INDEX IX_SellOutDetails_ImportedAt ON dbo.SellOutDetails(ImportedAt);

PRINT 'SellOutDetails şeması uygulandı (var olanlar atlandı).';
