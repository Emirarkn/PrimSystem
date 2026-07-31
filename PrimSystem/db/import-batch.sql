-- ImportBatches: "Yükleme Kütüphanesi" — yüklenen her dosyanın ham içeriği + meta'sı.
-- Yükleme yıkıcı değil; geçmiş bir dosya tek tıkla yeniden aktifleştirilebilir (Replace/Append).
-- Idempotent — tekrar çalıştırmak güvenli.

IF OBJECT_ID('dbo.ImportBatches', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ImportBatches
    (
        BatchId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImportBatches PRIMARY KEY,
        FileType     NVARCHAR(20)  NOT NULL,                 -- sellout|order|target|ruta|visitplan|productmap|brickmap
        FileName     NVARCHAR(260) NOT NULL,
        FileHash     NVARCHAR(64)  NULL,
        Content      VARBINARY(MAX) NOT NULL,                -- ham dosya (yeniden yükleme için)
        Mode         NVARCHAR(10)  NOT NULL CONSTRAINT DF_ImportBatches_Mode DEFAULT ('Replace'),  -- Replace|Append
        [Year]       INT           NULL,
        Quarter      NVARCHAR(4)   NULL,
        IsActive     BIT           NOT NULL CONSTRAINT DF_ImportBatches_IsActive DEFAULT (0),
        RowCount     INT           NULL,
        QtyTotal     DECIMAL(18,2) NULL,
        UploadedAt   DATETIME2     NOT NULL CONSTRAINT DF_ImportBatches_UploadedAt DEFAULT (SYSUTCDATETIME()),
        UploadedById INT           NULL,
        Status       NVARCHAR(20)  NOT NULL CONSTRAINT DF_ImportBatches_Status DEFAULT ('Active')
    );

    CREATE INDEX IX_ImportBatches_Type_Active ON dbo.ImportBatches (FileType, IsActive);
END
GO
