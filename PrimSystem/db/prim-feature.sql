/* ============================================================================
   PrimSystem — Prim Tutarı Özelliği Şema Değişikliği
   ----------------------------------------------------------------------------
   ÖNEMLİ: Uygulama bu DDL uygulanmadan ÇALIŞMAZ — Calculation sorguları
   PrimAmount kolonunu, hesaplama da PrimTiers/RolePrimBases tablolarını okur.
   Yeni sürümü dağıtmadan ÖNCE bu scripti çalıştırın.

   Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -i prim-feature.sql
            ya da SSMS'te PrimDB üzerinde açıp Execute.

   Tümü IDEMPOTENT'tir (varsa atlanır).
   ============================================================================ */

SET NOCOUNT ON;

/* 1) Calculations.PrimAmount — hesaplanan prim tutarı (TL) */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name='PrimAmount' AND object_id=OBJECT_ID('dbo.Calculations'))
    ALTER TABLE dbo.Calculations ADD PrimAmount DECIMAL(18,2) NULL;

/* 2) PrimTiers — skor aralığı → taban tutar çarpanı */
IF OBJECT_ID('dbo.PrimTiers','U') IS NULL
BEGIN
    CREATE TABLE dbo.PrimTiers (
        TierId      INT IDENTITY(1,1) PRIMARY KEY,
        MinScore    DECIMAL(18,4) NOT NULL,
        MaxScore    DECIMAL(18,4) NOT NULL,
        Multiplier  DECIMAL(18,4) NOT NULL,
        IsActive    BIT NOT NULL CONSTRAINT DF_PrimTiers_IsActive DEFAULT(1),
        CreatedAt   DATETIME2 NOT NULL CONSTRAINT DF_PrimTiers_CreatedAt DEFAULT(SYSUTCDATETIME()),
        UpdatedBy   INT NULL
    );
END

/* 3) RolePrimBases — rol bazlı prim taban tutarı */
IF OBJECT_ID('dbo.RolePrimBases','U') IS NULL
BEGIN
    CREATE TABLE dbo.RolePrimBases (
        Id          INT IDENTITY(1,1) PRIMARY KEY,
        RoleId      INT NOT NULL,
        BaseAmount  DECIMAL(18,2) NOT NULL,
        UpdatedAt   DATETIME2 NOT NULL CONSTRAINT DF_RolePrimBases_UpdatedAt DEFAULT(SYSUTCDATETIME()),
        UpdatedBy   INT NULL,
        CONSTRAINT FK_RolePrimBases_Roles FOREIGN KEY (RoleId) REFERENCES dbo.Roles(RoleId)
    );
    CREATE UNIQUE INDEX UX_RolePrimBases_Role ON dbo.RolePrimBases(RoleId);
END

/* 4) Başlangıç verisi — örnek kademeler (gate %85 ve üzeri). İsteğe bağlı; düzenlenebilir. */
IF NOT EXISTS (SELECT 1 FROM dbo.PrimTiers)
BEGIN
    INSERT INTO dbo.PrimTiers (MinScore, MaxScore, Multiplier) VALUES
        (0.85, 0.9499, 0.50),
        (0.95, 0.9999, 0.75),
        (1.00, 9.9999, 1.00);
END

/* 5) ST rolü için örnek taban tutar (RoleId=4 = ST). Gerçek tutarla güncelleyin. */
IF NOT EXISTS (SELECT 1 FROM dbo.RolePrimBases WHERE RoleId = 4)
    INSERT INTO dbo.RolePrimBases (RoleId, BaseAmount) VALUES (4, 0);

PRINT 'Prim özelliği şeması uygulandı (var olanlar atlandı).';
