using Microsoft.EntityFrameworkCore;
using System.Data;

namespace PrimSystem.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        // Core
        public DbSet<Role> Roles => Set<Role>();
        public DbSet<Region> Regions => Set<Region>();
        public DbSet<User> Users => Set<User>();
        public DbSet<Brick> Bricks => Set<Brick>();
        public DbSet<UserBrick> UserBricks => Set<UserBrick>();
        public DbSet<Institution> Institutions => Set<Institution>();

        // Product & Scorecard
        public DbSet<Product> Products => Set<Product>();
        public DbSet<ScorecardWeight> ScorecardWeights => Set<ScorecardWeight>();
        public DbSet<KpiCriteria> KPICriteria => Set<KpiCriteria>();

        // Data
        public DbSet<Period> Periods => Set<Period>();
        public DbSet<Target> Targets => Set<Target>();
        public DbSet<SellOut> SellOuts => Set<SellOut>();
        public DbSet<SellOutDetail> SellOutDetails => Set<SellOutDetail>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<RutaUyum> RutaUyum => Set<RutaUyum>();
        public DbSet<VisitPlan> VisitPlans => Set<VisitPlan>();
        public DbSet<PharmacyPlan> PharmacyPlans => Set<PharmacyPlan>();

        // Calculations
        public DbSet<Calculation> Calculations => Set<Calculation>();
        public DbSet<CalculationDetail> CalculationDetails => Set<CalculationDetail>();

        // Tracking
        public DbSet<ImportLog> ImportLogs => Set<ImportLog>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<UserSession> UserSessions => Set<UserSession>();
        public DbSet<OrderLeaderboardPublish> OrderLeaderboardPublishes => Set<OrderLeaderboardPublish>();
        public DbSet<StarCriterion> StarCriteria => Set<StarCriterion>();
        public DbSet<PrimTier> PrimTiers => Set<PrimTier>();
        public DbSet<RolePrimBase> RolePrimBases => Set<RolePrimBase>();
        public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
        public DbSet<NameAlias> NameAliases => Set<NameAlias>();
        public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
        public DbSet<StPhoto> StPhotos => Set<StPhoto>();

        // Collaboration
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<Event> Events => Set<Event>();
        public DbSet<CalendarEntry> CalendarEntries => Set<CalendarEntry>();
        public DbSet<Announcement> Announcements => Set<Announcement>();
        public DbSet<TaskItem> Tasks => Set<TaskItem>();
        public DbSet<TaskComment> TaskComments => Set<TaskComment>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Global query filter: silinmiş kullanıcılar otomatik hariç tutulur
            // Gerektiğinde: db.Users.IgnoreQueryFilters().Where(...)
            modelBuilder.Entity<User>().HasQueryFilter(u => !u.IsDeleted);

            // UserBricks composite key
            modelBuilder.Entity<UserBrick>().HasKey(ub => new { ub.UserId, ub.BrickId });
            // Ortak brick payı: tek sahip = 100 (varsayılan). decimal(5,2).
            modelBuilder.Entity<UserBrick>().Property(ub => ub.SharePercent)
                .HasPrecision(5, 2).HasDefaultValue(100m);
            // Ağırlık kaydı oluşturma zamanı — freshness için; DB default (mevcut satırlar dolar).
            modelBuilder.Entity<ScorecardWeight>().Property(w => w.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            // Table name mappings
            modelBuilder.Entity<Role>().ToTable("Roles");
            modelBuilder.Entity<Region>().ToTable("Regions");
            modelBuilder.Entity<User>().ToTable("Users");
            modelBuilder.Entity<Brick>().ToTable("Bricks");
            modelBuilder.Entity<UserBrick>().ToTable("UserBricks");
            modelBuilder.Entity<Institution>().ToTable("Institutions");
            modelBuilder.Entity<Institution>().HasIndex(i => i.GLN).IsUnique().HasFilter("[GLN] IS NOT NULL");
            modelBuilder.Entity<Product>().ToTable("Products");
            modelBuilder.Entity<ScorecardWeight>().ToTable("ScorecardWeights");
            modelBuilder.Entity<KpiCriteria>().ToTable("KPICriteria");
            modelBuilder.Entity<Period>().ToTable("Periods");
            modelBuilder.Entity<Target>().ToTable("Targets");
            modelBuilder.Entity<SellOut>().ToTable("SellOuts");
            modelBuilder.Entity<SellOutDetail>().ToTable("SellOutDetails");
            modelBuilder.Entity<Order>().ToTable("Orders");
            modelBuilder.Entity<RutaUyum>().ToTable("RutaUyum");
            modelBuilder.Entity<VisitPlan>().ToTable("VisitPlans");
            modelBuilder.Entity<PharmacyPlan>().ToTable("PharmacyPlans");
            // GLN nvarchar(450) — indekslenebilsin (Institutions.GLN ile aynı gerekçe).
            modelBuilder.Entity<PharmacyPlan>().Property(p => p.GLN).HasMaxLength(450).IsRequired();
            modelBuilder.Entity<PharmacyPlan>().Property(p => p.Segment).HasMaxLength(20);
            modelBuilder.Entity<PharmacyPlan>().Property(p => p.KurumName).HasMaxLength(200);
            modelBuilder.Entity<PharmacyPlan>().Property(p => p.SmmId).HasMaxLength(50);
            // Analiz anti-join'i (planlı/plansız) bu indeksten geçiyor — VisitPlans'ta indeks
            // olmaması mevcut bir eksiklik, burada tekrarlanmıyor.
            modelBuilder.Entity<PharmacyPlan>().HasIndex(p => new { p.Year, p.GLN });
            modelBuilder.Entity<PharmacyPlan>().HasIndex(p => p.UserId);
            modelBuilder.Entity<Calculation>().ToTable("Calculations");
            modelBuilder.Entity<CalculationDetail>().ToTable("CalculationDetails");
            modelBuilder.Entity<ImportLog>().ToTable("ImportLogs");
            modelBuilder.Entity<AuditLog>().ToTable("AuditLogs");
            modelBuilder.Entity<Notification>().ToTable("Notifications");
            modelBuilder.Entity<Event>().ToTable("Events");
            modelBuilder.Entity<CalendarEntry>().ToTable("CalendarEntries");
            modelBuilder.Entity<Announcement>().ToTable("Announcements");
            modelBuilder.Entity<TaskItem>().ToTable("Tasks");
            modelBuilder.Entity<TaskComment>().ToTable("TaskComments");

            // Cascade delete kuralları — mevcut User navigation property üzerinden
            // (HasOne<User>() boş kullanıldığında EF shadow FK UserId1 oluşturur — bunu önle)
            modelBuilder.Entity<UserBrick>()
                .HasOne(ub => ub.User).WithMany().HasForeignKey(ub => ub.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<UserSession>()
                .HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Hesaplamalar: veri bütünlüğü için koru
            modelBuilder.Entity<Calculation>()
                .HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            // Bildirimler: kullanıcıyla birlikte sil
            modelBuilder.Entity<Notification>()
                .HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Period computed column (read-only)
            modelBuilder.Entity<Period>().Property(p => p.Quarter).ValueGeneratedOnAddOrUpdate();

            // Decimal precision — SQL Server truncation önleme
            modelBuilder.Entity<Calculation>().Property(e => e.TotalScore).HasPrecision(18, 4);
            modelBuilder.Entity<Calculation>().Property(e => e.RutaScore).HasPrecision(18, 4);
            modelBuilder.Entity<Calculation>().Property(e => e.OrderScore).HasPrecision(18, 4);
            modelBuilder.Entity<Calculation>().Property(e => e.DistScore).HasPrecision(18, 4);
            modelBuilder.Entity<Calculation>().Property(e => e.StrikeScore).HasPrecision(18, 4);
            modelBuilder.Entity<Calculation>().Property(e => e.FinalScore).HasPrecision(18, 4);
            modelBuilder.Entity<Calculation>().Property(e => e.PrimAmount).HasPrecision(18, 2);

            modelBuilder.Entity<PrimTier>().Property(e => e.MinScore).HasPrecision(18, 4);
            modelBuilder.Entity<PrimTier>().Property(e => e.MaxScore).HasPrecision(18, 4);
            modelBuilder.Entity<PrimTier>().Property(e => e.Multiplier).HasPrecision(18, 4);
            modelBuilder.Entity<RolePrimBase>().Property(e => e.BaseAmount).HasPrecision(18, 2);

            modelBuilder.Entity<CalculationDetail>().Property(e => e.TargetQty).HasPrecision(18, 4);
            modelBuilder.Entity<CalculationDetail>().Property(e => e.ActualQty).HasPrecision(18, 4);
            modelBuilder.Entity<CalculationDetail>().Property(e => e.Rate).HasPrecision(18, 4);
            modelBuilder.Entity<CalculationDetail>().Property(e => e.CappedRate).HasPrecision(18, 4);
            modelBuilder.Entity<CalculationDetail>().Property(e => e.Score).HasPrecision(18, 4);

            modelBuilder.Entity<Order>().Property(e => e.BoxQuantity).HasPrecision(18, 4);
            modelBuilder.Entity<Order>().Property(e => e.AmountTL).HasPrecision(18, 4);
            modelBuilder.Entity<Order>().Property(e => e.MFTL).HasPrecision(18, 4);

            modelBuilder.Entity<RutaUyum>().Property(e => e.Rate).HasPrecision(18, 4);

            modelBuilder.Entity<ScorecardWeight>().Property(e => e.MinRate).HasPrecision(18, 4);

            modelBuilder.Entity<SellOut>().Property(e => e.Quantity).HasPrecision(18, 4);
            modelBuilder.Entity<SellOut>().Property(e => e.AmountTL).HasPrecision(18, 4);
            modelBuilder.Entity<SellOut>().Property(e => e.MFTL).HasPrecision(18, 4);

            modelBuilder.Entity<SellOutDetail>().Property(e => e.Quantity).HasPrecision(18, 4);
            modelBuilder.Entity<SellOutDetail>().Property(e => e.AmountTL).HasPrecision(18, 4);
            modelBuilder.Entity<SellOutDetail>().Property(e => e.MFTL).HasPrecision(18, 4);
            modelBuilder.Entity<SellOutDetail>().HasIndex(e => new { e.PeriodId, e.BrickId, e.ProductId });
            modelBuilder.Entity<SellOutDetail>().HasIndex(e => e.GLN);
            // Eczane Analizi: dönem filtreli GLN-bazında toplama için kapsayıcı indeks (key-lookup'sız).
            modelBuilder.Entity<SellOutDetail>().HasIndex(e => e.PeriodId)
                .HasDatabaseName("IX_SellOutDetails_Period_Incl")
                .IncludeProperties(e => new { e.GLN, e.BrickId, e.Quantity, e.AmountTL, e.KurumName, e.Il, e.Ilce });
            modelBuilder.Entity<SellOutDetail>().HasIndex(e => e.ImportedAt);

            modelBuilder.Entity<Target>().Property(e => e.TargetQty).HasPrecision(18, 4);

            // UserSession & OrderLeaderboardPublish & StarCriteria
            modelBuilder.Entity<UserSession>().ToTable("UserSessions");
            modelBuilder.Entity<OrderLeaderboardPublish>().ToTable("OrderLeaderboardPublish");
            modelBuilder.Entity<StarCriterion>().ToTable("StarCriteria");
            modelBuilder.Entity<PrimTier>().ToTable("PrimTiers");
            modelBuilder.Entity<RolePrimBase>().ToTable("RolePrimBases");
            modelBuilder.Entity<SystemSetting>().ToTable("SystemSettings");
            modelBuilder.Entity<SystemSetting>().HasIndex(s => s.Key).IsUnique();
            modelBuilder.Entity<NameAlias>().ToTable("NameAliases");
            modelBuilder.Entity<NameAlias>().HasIndex(a => new { a.AliasType, a.AliasKey }).IsUnique();
            modelBuilder.Entity<ImportBatch>().ToTable("ImportBatches");
            modelBuilder.Entity<ImportBatch>().HasIndex(b => new { b.FileType, b.IsActive });
            modelBuilder.Entity<ImportBatch>().Property(b => b.QtyTotal).HasPrecision(18, 2);

            // StPhoto — iki ayrı User FK (yükleyen + onaylayan); çoklu cascade yolundan kaçın.
            modelBuilder.Entity<StPhoto>().ToTable("StPhotos");
            modelBuilder.Entity<StPhoto>()
                .HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<StPhoto>()
                .HasOne(p => p.Reviewer).WithMany().HasForeignKey(p => p.ReviewedBy)
                .OnDelete(DeleteBehavior.NoAction);
            modelBuilder.Entity<StPhoto>().HasIndex(p => new { p.UserId, p.CreatedAt });
            modelBuilder.Entity<StPhoto>().HasIndex(p => p.Status);
        }
    }
}