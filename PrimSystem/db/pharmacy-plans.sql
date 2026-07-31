-- PharmacyPlans: saha ziyaret planına ALINMIŞ eczaneler (UTT_Brick raporu, ECZANE sayfası).
-- Amaç: analizlerde "planlı" eczaneleri ana eczane kütlesinden ayırabilmek.
--   planlı  = Institutions'ta olup PharmacyPlans'ta DA olan GLN
--   plansız = Institutions'ta olup PharmacyPlans'ta OLMAYAN GLN (anti-join)
--
-- Neden VisitPlans'a yazılmıyor: VisitPlans, Strike Rate KPI'ının girdisi. Oraya yazmak prim
-- TUTARINI değiştirmez ((orderScore ?? 0) * w — null da 0 da aynı) ama ekranda Strike Rate'i
-- "—" yerine "%0" gösterir. Bu tablo hiçbir KPI'ya girmez, yalnız analiz/işaretleme içindir.
--
-- Institutions'a FK YOK: hiç satışı olmamış planlı eczaneler de saklanabilsin (kaynak dosyada
-- 134 GLN Institutions'ta yok — Institutions = SellOutDetails distinct GLN olduğu için bunlar
-- hiç sell-out'u olmayan eczaneler).
--
-- Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -i pharmacy-plans.sql
--          ya da SSMS'te PrimDB üzerinde açıp Execute (dosyayı UTF-8 aç).
-- Idempotent — tekrar çalıştırmak güvenlidir.

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.PharmacyPlans') IS NULL
BEGIN
    CREATE TABLE dbo.PharmacyPlans
    (
        PlanId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PharmacyPlans PRIMARY KEY,
        GLN           NVARCHAR(450)  NOT NULL,
        UserId        INT            NULL,   -- satırdaki "Çalışan ÜTT"; eşleşmezse NULL
        BrickId       INT            NULL,
        [Year]        INT            NOT NULL,
        Segment       NVARCHAR(20)   NULL,   -- 'A(2)' / 'B(1)' / '-'
        Frequency     INT            NOT NULL CONSTRAINT DF_PharmacyPlans_Frequency DEFAULT (0),
        PlannedVisits INT            NOT NULL CONSTRAINT DF_PharmacyPlans_Planned   DEFAULT (0),
        ActualVisits  INT            NOT NULL CONSTRAINT DF_PharmacyPlans_Actual    DEFAULT (0),
        KurumName     NVARCHAR(200)  NULL,
        SmmId         NVARCHAR(50)   NULL,
        ImportedAt    DATETIME2      NOT NULL CONSTRAINT DF_PharmacyPlans_ImportedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_PharmacyPlans_Users  FOREIGN KEY (UserId)  REFERENCES dbo.Users(UserId),
        CONSTRAINT FK_PharmacyPlans_Bricks FOREIGN KEY (BrickId) REFERENCES dbo.Bricks(BrickId)
    );
END
GO

-- Analiz anti-join'inin (planlı/plansız) geçtiği indeks.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_PharmacyPlans_Year_GLN' AND object_id = OBJECT_ID('dbo.PharmacyPlans'))
    CREATE INDEX IX_PharmacyPlans_Year_GLN ON dbo.PharmacyPlans([Year], GLN);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_PharmacyPlans_UserId' AND object_id = OBJECT_ID('dbo.PharmacyPlans'))
    CREATE INDEX IX_PharmacyPlans_UserId ON dbo.PharmacyPlans(UserId);
GO

-- Import upsert anahtarı. UNIQUE DEĞİL: UserId/BrickId NULL olabildiği için (eşleşmeyen ST/brick)
-- unique index NULL'ları tek satıra indirger ve ikinci eşleşmeyen satırı reddeder.
-- Tekilliği import kodu (touchedThisBatch deseni) sağlıyor; bu indeks yalnız arama içindir.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_PharmacyPlans_Key' AND object_id = OBJECT_ID('dbo.PharmacyPlans'))
    CREATE INDEX IX_PharmacyPlans_Key ON dbo.PharmacyPlans([Year], GLN, UserId, BrickId);
GO

PRINT 'PharmacyPlans hazır. SONRAKİ ADIM: /import → "Eczane Planı (UTT_Brick)" ile yükleyin.';
GO
