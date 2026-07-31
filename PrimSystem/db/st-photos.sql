/* ============================================================================
   PrimSystem — Saha Fotoğrafı Modülü Şema Değişikliği
   ----------------------------------------------------------------------------
   ÖNEMLİ: Foto yükleme/kontrol ekranları bu DDL uygulanmadan ÇALIŞMAZ.
   PhotoService tabloyu okur; tablo yoksa servis güvenli (boş) döner ama
   yükleme/onay yapılamaz. Yeni sürümü dağıtmadan ÖNCE bu scripti çalıştırın.

   Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -i st-photos.sql
            ya da SSMS'te PrimDB üzerinde açıp Execute.

   Tümü IDEMPOTENT'tir (varsa atlanır).
   ============================================================================ */

SET NOCOUNT ON;

/* 1) StPhotos — saha fotoğrafları (görüntü DB'de varbinary olarak saklanır) */
IF OBJECT_ID('dbo.StPhotos','U') IS NULL
BEGIN
    CREATE TABLE dbo.StPhotos (
        Id          INT IDENTITY(1,1) PRIMARY KEY,
        UserId      INT NOT NULL,                 -- yükleyen ST
        Title       NVARCHAR(200) NOT NULL,
        Purpose     NVARCHAR(MAX) NULL,           -- amaç/açıklama
        FileName    NVARCHAR(260) NOT NULL,
        ContentType NVARCHAR(100) NOT NULL,       -- image/png | image/jpeg | image/webp
        FileSize    BIGINT NOT NULL,
        ImageData   VARBINARY(MAX) NOT NULL,
        Status      NVARCHAR(20) NOT NULL CONSTRAINT DF_StPhotos_Status DEFAULT('Pending'),
        ReviewNote  NVARCHAR(MAX) NULL,
        ReviewedBy  INT NULL,
        ReviewedAt  DATETIME2 NULL,
        CreatedAt   DATETIME2 NOT NULL CONSTRAINT DF_StPhotos_CreatedAt DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_StPhotos_Users_UserId
            FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId) ON DELETE CASCADE,
        CONSTRAINT FK_StPhotos_Users_ReviewedBy
            FOREIGN KEY (ReviewedBy) REFERENCES dbo.Users(UserId)
    );
    CREATE INDEX IX_StPhotos_UserId_CreatedAt ON dbo.StPhotos(UserId, CreatedAt);
    CREATE INDEX IX_StPhotos_Status ON dbo.StPhotos(Status);
END

PRINT 'StPhotos şeması uygulandı (var olanlar atlandı).';
