-- NameAliases: import eşleştirmesinde hatırlanan kaynak-adı → kanonik varlık (Brick/ST) eşlemesi.
-- Önizlemedeki "bunu mu demek istediniz" önerisi tek tıkla onaylanınca buraya yazılır;
-- sonraki importlarda kaynak dosyadaki yazım varyantı otomatik çözülür.
-- Idempotent — tekrar çalıştırmak güvenli.

IF OBJECT_ID('dbo.NameAliases', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NameAliases
    (
        NameAliasId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NameAliases PRIMARY KEY,
        AliasType   NVARCHAR(20)  NOT NULL,                 -- 'Brick' | 'User'
        AliasKey    NVARCHAR(200) NOT NULL,                 -- Normalize edilmiş kaynak ad
        TargetId    INT           NOT NULL,                 -- BrickId / UserId
        CreatedAt   DATETIME2     NOT NULL CONSTRAINT DF_NameAliases_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy   INT           NULL
    );

    CREATE UNIQUE INDEX UX_NameAliases_Type_Key ON dbo.NameAliases (AliasType, AliasKey);
END
GO
