/* ============================================================================
   PrimSystem — Genel Sistem Ayarları (anahtar-değer) Şema Değişikliği
   ----------------------------------------------------------------------------
   ÖNEMLİ: Uygulama bu DDL uygulanmadan ÇALIŞMAZ — Home/Reports panoları
   "SalesOnlyMode" ayarını SystemSettings tablosundan okur.
   Yeni sürümü dağıtmadan ÖNCE bu scripti çalıştırın.

   Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -i system-settings.sql
            ya da SSMS'te PrimDB üzerinde açıp Execute.

   Tümü IDEMPOTENT'tir (varsa atlanır).
   ============================================================================ */

SET NOCOUNT ON;

/* 1) SystemSettings — genel anahtar-değer ayar tablosu */
IF OBJECT_ID('dbo.SystemSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.SystemSettings (
        Id          INT IDENTITY(1,1) PRIMARY KEY,
        [Key]       NVARCHAR(450) NOT NULL,   -- EF modeliyle eşleşir (indeksli string)
        [Value]     NVARCHAR(MAX) NOT NULL,
        UpdatedAt   DATETIME2 NOT NULL CONSTRAINT DF_SystemSettings_UpdatedAt DEFAULT(SYSUTCDATETIME()),
        UpdatedBy   INT NULL
    );
    CREATE UNIQUE INDEX UX_SystemSettings_Key ON dbo.SystemSettings([Key]);
END

PRINT 'SystemSettings şeması uygulandı (var olanlar atlandı).';
