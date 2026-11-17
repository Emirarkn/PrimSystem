using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrimSystem.Migrations
{
    /// <summary>
    /// Modelde olup hiçbir migration'da bulunmayan şemayı ekler: ScorecardWeights.UpdatedAt,
    /// KPICriteria.UpdatedAt ve PharmacyPlans. Bu nesneler mevcut veritabanlarında Program.cs
    /// self-heal bloğu veya elle çalıştırılan db/*.sql scriptleriyle zaten oluşmuş olabilir; bu yüzden
    /// her adım koruma koşulludur (idempotent). Yalnızca migration'la kurulan boş bir veritabanı da
    /// eksiksiz şemaya kavuşur (F09 / COR-008).
    /// </summary>
    public partial class SyncModelWithSelfHeal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ScorecardWeights') AND name = N'UpdatedAt')
    ALTER TABLE dbo.ScorecardWeights ADD UpdatedAt datetime2 NULL;");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.KPICriteria') AND name = N'UpdatedAt')
    ALTER TABLE dbo.KPICriteria ADD UpdatedAt datetime2 NULL;");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'dbo.PharmacyPlans', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PharmacyPlans (
        PlanId        bigint IDENTITY(1,1) NOT NULL,
        GLN           nvarchar(450) NOT NULL,
        UserId        int NULL,
        BrickId       int NULL,
        Year          int NOT NULL,
        Segment       nvarchar(20) NULL,
        Frequency     int NOT NULL,
        PlannedVisits int NOT NULL,
        ActualVisits  int NOT NULL,
        KurumName     nvarchar(200) NULL,
        SmmId         nvarchar(50) NULL,
        ImportedAt    datetime2 NOT NULL,
        CONSTRAINT PK_PharmacyPlans PRIMARY KEY (PlanId),
        CONSTRAINT FK_PharmacyPlans_Bricks_BrickId FOREIGN KEY (BrickId) REFERENCES dbo.Bricks (BrickId),
        CONSTRAINT FK_PharmacyPlans_Users_UserId FOREIGN KEY (UserId) REFERENCES dbo.Users (UserId)
    );
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PharmacyPlans') AND name = N'IX_PharmacyPlans_BrickId')
    CREATE INDEX IX_PharmacyPlans_BrickId ON dbo.PharmacyPlans (BrickId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PharmacyPlans') AND name = N'IX_PharmacyPlans_UserId')
    CREATE INDEX IX_PharmacyPlans_UserId ON dbo.PharmacyPlans (UserId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PharmacyPlans') AND name = N'IX_PharmacyPlans_Year_GLN')
    CREATE INDEX IX_PharmacyPlans_Year_GLN ON dbo.PharmacyPlans (Year, GLN);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF OBJECT_ID(N'dbo.PharmacyPlans', N'U') IS NOT NULL DROP TABLE dbo.PharmacyPlans;");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ScorecardWeights') AND name = N'UpdatedAt')
    ALTER TABLE dbo.ScorecardWeights DROP COLUMN UpdatedAt;");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.KPICriteria') AND name = N'UpdatedAt')
    ALTER TABLE dbo.KPICriteria DROP COLUMN UpdatedAt;");
        }
    }
}
