/* ============================================================================
   PrimSystem — Performans İndeksleri
   ----------------------------------------------------------------------------
   Şema EF migration ile değil elle yönetildiği için indeksler bu script ile
   uygulanır. Tümü IDEMPOTENT'tir (varsa atlanır) — tekrar tekrar çalıştırılabilir.

   Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -i indexes.sql
            ya da SSMS'te bu dosyayı açıp PrimDB üzerinde Execute.

   İndeksler CalculationService raw SQL'leri ve EF sorgu desenlerine göre seçildi
   (PeriodId/BrickId/ProductId üzerinden yoğun GROUP BY + JOIN, Year/Quarter
   filtreleri, rollback için ImportedAt tam eşleşme).
   ============================================================================ */

SET NOCOUNT ON;
-- Hesaplanan kolon içeren tablolarda (ör. Periods.Quarter) indeks oluşturmak için gerekli
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

/* --- Yardımcı makro yerine tek tek IF NOT EXISTS kontrolü --- */

/* Targets: WHERE PeriodId IN (...) GROUP BY UserId, ProductId */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Targets_Period_User_Product' AND object_id=OBJECT_ID('dbo.Targets'))
    CREATE INDEX IX_Targets_Period_User_Product ON dbo.Targets(PeriodId, UserId, ProductId) INCLUDE(TargetQty);

/* SellOuts: WHERE PeriodId IN AND Quantity>0; JOIN UserBricks ON BrickId */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SellOuts_Period_Brick_Product' AND object_id=OBJECT_ID('dbo.SellOuts'))
    CREATE INDEX IX_SellOuts_Period_Brick_Product ON dbo.SellOuts(PeriodId, BrickId, ProductId) INCLUDE(Quantity);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SellOuts_ImportedAt' AND object_id=OBJECT_ID('dbo.SellOuts'))
    CREATE INDEX IX_SellOuts_ImportedAt ON dbo.SellOuts(ImportedAt);

/* Orders: WHERE PeriodId IN; JOIN UserBricks ON BrickId; ProductId filtresi; GROUP BY UserId */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Orders_Period_Brick' AND object_id=OBJECT_ID('dbo.Orders'))
    CREATE INDEX IX_Orders_Period_Brick ON dbo.Orders(PeriodId, BrickId) INCLUDE(ProductId, UserId, BoxQuantity, AmountTL, MFTL, InstitutionCount, GLN);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Orders_Product' AND object_id=OBJECT_ID('dbo.Orders'))
    CREATE INDEX IX_Orders_Product ON dbo.Orders(ProductId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Orders_ImportedAt' AND object_id=OBJECT_ID('dbo.Orders'))
    CREATE INDEX IX_Orders_ImportedAt ON dbo.Orders(ImportedAt);

/* UserBricks: WHERE IsActive=1; JOIN ON BrickId (PK zaten UserId,BrickId) */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_UserBricks_Brick_Active' AND object_id=OBJECT_ID('dbo.UserBricks'))
    CREATE INDEX IX_UserBricks_Brick_Active ON dbo.UserBricks(BrickId, IsActive) INCLUDE(UserId);

/* Calculations: WHERE Year, Quarter; IsPublished/IsPaused; ORDER BY FinalScore */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Calculations_Year_Quarter' AND object_id=OBJECT_ID('dbo.Calculations'))
    CREATE INDEX IX_Calculations_Year_Quarter ON dbo.Calculations(Year, Quarter) INCLUDE(UserId, IsPublished, IsPaused, FinalScore);

/* CalculationDetails: WHERE CalcId IN (...) */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CalculationDetails_Calc' AND object_id=OBJECT_ID('dbo.CalculationDetails'))
    CREATE INDEX IX_CalculationDetails_Calc ON dbo.CalculationDetails(CalcId);

/* RutaUyum: WHERE Year, Quarter (+ rollback ImportedAt) */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_RutaUyum_Year_Quarter' AND object_id=OBJECT_ID('dbo.RutaUyum'))
    CREATE INDEX IX_RutaUyum_Year_Quarter ON dbo.RutaUyum(Year, Quarter) INCLUDE(UserId, Rate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_RutaUyum_ImportedAt' AND object_id=OBJECT_ID('dbo.RutaUyum'))
    CREATE INDEX IX_RutaUyum_ImportedAt ON dbo.RutaUyum(ImportedAt);

/* VisitPlans: WHERE Year, Quarter, UserId (+ rollback ImportedAt) */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_VisitPlans_Year_Quarter_User' AND object_id=OBJECT_ID('dbo.VisitPlans'))
    CREATE INDEX IX_VisitPlans_Year_Quarter_User ON dbo.VisitPlans(Year, Quarter, UserId) INCLUDE(GLN);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_VisitPlans_ImportedAt' AND object_id=OBJECT_ID('dbo.VisitPlans'))
    CREATE INDEX IX_VisitPlans_ImportedAt ON dbo.VisitPlans(ImportedAt);

/* Periods: WHERE Year AND Month IN (...) */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Periods_Year_Month' AND object_id=OBJECT_ID('dbo.Periods'))
    CREATE INDEX IX_Periods_Year_Month ON dbo.Periods(Year, Month);

/* Users: login (Username/Email), rol/aktiflik filtreleri */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Users_Username' AND object_id=OBJECT_ID('dbo.Users'))
    CREATE INDEX IX_Users_Username ON dbo.Users(Username);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Users_Email' AND object_id=OBJECT_ID('dbo.Users'))
    CREATE INDEX IX_Users_Email ON dbo.Users(Email);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Users_Role_Active' AND object_id=OBJECT_ID('dbo.Users'))
    CREATE INDEX IX_Users_Role_Active ON dbo.Users(RoleId, IsActive, IsDeleted);

/* UserSessions: WHERE UserId AND IsActive=1 */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_UserSessions_User_Active' AND object_id=OBJECT_ID('dbo.UserSessions'))
    CREATE INDEX IX_UserSessions_User_Active ON dbo.UserSessions(UserId, IsActive);

/* Notifications: WHERE UserId AND IsRead; ORDER BY CreatedAt */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Notifications_User_Read' AND object_id=OBJECT_ID('dbo.Notifications'))
    CREATE INDEX IX_Notifications_User_Read ON dbo.Notifications(UserId, IsRead) INCLUDE(CreatedAt);

/* ImportLogs: listeleme ORDER BY ImportedAt DESC */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ImportLogs_ImportedAt' AND object_id=OBJECT_ID('dbo.ImportLogs'))
    CREATE INDEX IX_ImportLogs_ImportedAt ON dbo.ImportLogs(ImportedAt);

/* AuditLogs: WHERE UserId; ORDER BY CreatedAt DESC */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AuditLogs_User_Created' AND object_id=OBJECT_ID('dbo.AuditLogs'))
    CREATE INDEX IX_AuditLogs_User_Created ON dbo.AuditLogs(UserId, CreatedAt);

PRINT 'PrimSystem indeksleri uygulandı (var olanlar atlandı).';
