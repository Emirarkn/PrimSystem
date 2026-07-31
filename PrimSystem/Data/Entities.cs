using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrimSystem.Data
{
    public class Role
    {
        [Key] public int RoleId { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
    }

    public class Region
    {
        [Key] public int RegionId { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class User
    {
        [Key] public int UserId { get; set; }
        public string FullName { get; set; } = "";
        public string Username { get; set; } = "";
        public string Email { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public int RoleId { get; set; }
        public int? RegionId { get; set; }
        public int? ManagerId { get; set; }
        public DateTime HireDate { get; set; }
        public DateTime? BirthDate { get; set; }
        public int MinMonths { get; set; } = 3;
        public bool MustChangePassword { get; set; } = true;
        public bool IsActive { get; set; } = true;
        public bool IsDeleted { get; set; } = false;
        public DateTime? DeactivatedAt { get; set; }
        public int? DeactivatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastLoginAt { get; set; }
        public int? ActiveSessionId { get; set; }
        public string? ResetToken { get; set; }
        public DateTime? ResetTokenExpiry { get; set; }

        [ForeignKey("RoleId")] public Role? Role { get; set; }
        [ForeignKey("RegionId")] public Region? Region { get; set; }
        [ForeignKey("ManagerId")] public User? Manager { get; set; }
    }

    public class Brick
    {
        [Key] public int BrickId { get; set; }
        public string Name { get; set; } = "";
        public string? City { get; set; }
        public int? RegionId { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("RegionId")] public Region? Region { get; set; }
    }

    public class UserBrick
    {
        public int UserId { get; set; }
        public int BrickId { get; set; }
        public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UnassignedAt { get; set; }
        public bool IsActive { get; set; } = true;
        // Ortak brick payı (%). Tek sahip = 100. Bir brick'in aktif sahipleri toplam 100 olmalı.
        // Atıf (hedef/sell-out/sipariş) bu payla çarpılır; 100 → davranış değişmez.
        public decimal SharePercent { get; set; } = 100m;

        [ForeignKey("UserId")] public User? User { get; set; }
        [ForeignKey("BrickId")] public Brick? Brick { get; set; }
    }

    public class Institution
    {
        [Key] public int InstitutionId { get; set; }
        public string? GLN { get; set; }
        public string Name { get; set; } = "";
        public string? City { get; set; }            // İl
        public string? Ilce { get; set; }            // İlçe
        public int? BrickId { get; set; }
        public string? Segment { get; set; }
        /// <summary>Admin brick'i elle düzelttiyse true → sell-out importu BrickId'yi ezmez.</summary>
        public bool BrickOverride { get; set; } = false;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Bu GLN'in son görüldüğü sell-out import zamanı.</summary>
        public DateTime? LastSeenAt { get; set; }

        [ForeignKey("BrickId")] public Brick? Brick { get; set; }
    }

    public class Product
    {
        [Key] public int ProductId { get; set; }
        public string Name { get; set; } = "";
        public string? ImportAlias { get; set; }
        public string ProductGroup { get; set; } = "OTC";
        public bool IsPrimEligible { get; set; } = true;
        public int SortOrder { get; set; } = 0;
        public bool IsActive { get; set; } = true;
    }

    public class ScorecardWeight
    {
        [Key] public int WeightId { get; set; }
        public int ProductId { get; set; }
        public int Weight { get; set; }
        public decimal MinRate { get; set; } = 0.85m;
        public int Year { get; set; }
        public DateTime ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public int VersionNo { get; set; } = 1;
        public int? CreatedBy { get; set; }
        // Kaydın oluşturulma zamanı — hesaplama tazeliği (freshness) için: ağırlık son
        // hesaplamadan sonra girildiyse "Yeniden Hesapla gerekli" bandı tetiklenir.
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        // Son değişiklik zamanı. "Geriye dönük" düzenleme satırı YERİNDE günceller (yeni satır
        // eklenmez) → CreatedAt değişmez ve tazelik bandı sessiz kalırdı. Bu alan o boşluğu kapatır.
        public DateTime? UpdatedAt { get; set; }

        [ForeignKey("ProductId")] public Product? Product { get; set; }
    }

    public class KpiCriteria
    {
        [Key] public int CriteriaId { get; set; }
        public string Name { get; set; } = "";
        public int Weight { get; set; }
        public int MinRate { get; set; }
        public int Year { get; set; }
        public int SortOrder { get; set; } = 0;
        public DateTime ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public int VersionNo { get; set; } = 1;
        public int? CreatedBy { get; set; }
        // Son değişiklik zamanı — KPI ağırlığı yerinde güncellendiğinde tazelik bandını tetikler.
        public DateTime? UpdatedAt { get; set; }
    }

    public class Period
    {
        [Key] public int PeriodId { get; set; }
        public int Year { get; set; }
        public int Month { get; set; }
        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public string? Quarter { get; set; }
    }

    public class Target
    {
        [Key] public long TargetId { get; set; }
        public int UserId { get; set; }
        public int BrickId { get; set; }
        public int ProductId { get; set; }
        public int PeriodId { get; set; }
        public decimal TargetQty { get; set; }
        public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("UserId")] public User? User { get; set; }
        [ForeignKey("BrickId")] public Brick? Brick { get; set; }
        [ForeignKey("ProductId")] public Product? Product { get; set; }
        [ForeignKey("PeriodId")] public Period? Period { get; set; }
    }

    public class SellOut
    {
        [Key] public long SellOutId { get; set; }
        public int BrickId { get; set; }
        public int ProductId { get; set; }
        public int PeriodId { get; set; }
        public decimal Quantity { get; set; }
        public decimal? AmountTL { get; set; }
        public decimal? MFTL { get; set; }
        public int? UserId { get; set; }
        public DateTime? SnapshotDate { get; set; }
        public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("BrickId")] public Brick? Brick { get; set; }
        [ForeignKey("ProductId")] public Product? Product { get; set; }
        [ForeignKey("PeriodId")] public Period? Period { get; set; }
        [ForeignKey("UserId")] public User? User { get; set; }
    }

    // Sell-Out GLN/kurum kırılımı — SellOut tablosu brick+marka+dönem TOPLAMINI tutar (skor
    // için); bu tablo aynı verinin kurum (GLN) bazlı ham detayını saklar (analiz için).
    // Detayların PeriodId+BrickId+ProductId toplamı = ilgili SellOut.Quantity olmalı.
    public class SellOutDetail
    {
        [Key] public long SellOutDetailId { get; set; }
        public int BrickId { get; set; }
        public int ProductId { get; set; }
        public int PeriodId { get; set; }
        public string GLN { get; set; } = "";
        public string? KurumName { get; set; }
        public string? Il { get; set; }
        public string? Ilce { get; set; }
        public decimal Quantity { get; set; }
        public decimal? AmountTL { get; set; }
        public decimal? MFTL { get; set; }
        public int? UserId { get; set; }
        public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("BrickId")] public Brick? Brick { get; set; }
        [ForeignKey("ProductId")] public Product? Product { get; set; }
        [ForeignKey("PeriodId")] public Period? Period { get; set; }
        [ForeignKey("UserId")] public User? User { get; set; }
    }

    public class Order
    {
        [Key] public long OrderId { get; set; }
        public int BrickId { get; set; }
        public int ProductId { get; set; }
        public int PeriodId { get; set; }
        public string? Depot { get; set; }
        public int? InstitutionCount { get; set; }
        public decimal BoxQuantity { get; set; }
        public decimal? AmountTL { get; set; }
        public decimal? MFTL { get; set; }
        public int? UserId { get; set; }
        public string? GLN { get; set; }
        public DateTime? SnapshotDate { get; set; }
        public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("BrickId")] public Brick? Brick { get; set; }
        [ForeignKey("ProductId")] public Product? Product { get; set; }
        [ForeignKey("PeriodId")] public Period? Period { get; set; }
        [ForeignKey("UserId")] public User? User { get; set; }
    }

    public class VisitPlan
    {
        [Key] public int Id { get; set; }
        public string GLN { get; set; } = "";
        public int? UserId { get; set; }
        public int? BrickId { get; set; }
        public int Year { get; set; }
        public string Quarter { get; set; } = "";
        public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("UserId")] public User? User { get; set; }
        [ForeignKey("BrickId")] public Brick? Brick { get; set; }
    }

    /// <summary>
    /// Saha ziyaret planına ALINMIŞ eczane (UTT_Brick raporu, ECZANE sayfası). Bir satır =
    /// "bu GLN, bu yıl, bu ST'nin planında" + o plandaki ziyaret sayıları.
    ///
    /// Neden VisitPlans'tan ayrı: (a) VisitPlans Strike Rate KPI'ının girdisi — oraya yazmak
    /// skor ekranını etkiler; bu tablo YALNIZ analiz/işaretleme içindir, hiçbir KPI'ya girmez.
    /// (b) Devir nedeniyle aynı GLN iki ST'ye ait olabiliyor (dosyada 202 örnek), tek bit bunu
    /// temsil edemez. (c) Frek./Plan/Ziyaret verisi saklanır.
    ///
    /// "Plansız eczane" = Institutions'ta olup burada olmayan GLN (anti-join).
    /// Institutions'a FK YOK — hiç satışı olmamış planlı eczaneler de saklanabilsin diye.
    /// </summary>
    public class PharmacyPlan
    {
        [Key] public long PlanId { get; set; }
        public string GLN { get; set; } = "";
        /// <summary>Satırdaki "Çalışan ÜTT" — dosya gerçeği. Eşleşmezse null.</summary>
        public int? UserId { get; set; }
        public int? BrickId { get; set; }
        public int Year { get; set; }
        /// <summary>'A(2)' / 'B(1)' / '-' — kaynak dosyadaki segment.</summary>
        public string? Segment { get; set; }
        /// <summary>Aylık ziyaret frekansı (0/1/2/4).</summary>
        public int Frequency { get; set; }
        public int PlannedVisits { get; set; }
        /// <summary>Planlananın gerçekleşen kısmı — kaynak dosyada hiçbir satırda Plan'ı aşmıyor.</summary>
        public int ActualVisits { get; set; }
        public string? KurumName { get; set; }
        public string? SmmId { get; set; }
        public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("UserId")] public User? User { get; set; }
        [ForeignKey("BrickId")] public Brick? Brick { get; set; }
    }

    public class RutaUyum
    {
        [Key] public int RutaId { get; set; }
        public int UserId { get; set; }
        public int Year { get; set; }
        public string Quarter { get; set; } = "";
        public int PlannedVisits { get; set; }
        public int ActualVisits { get; set; }
        public decimal? Rate { get; set; }
        public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("UserId")] public User? User { get; set; }
    }

    public class Calculation
    {
        [Key] public int CalcId { get; set; }
        public int UserId { get; set; }
        public int Year { get; set; }
        public string Quarter { get; set; } = "";
        public decimal TotalScore { get; set; }
        public bool PassedGate { get; set; }
        public decimal? RutaScore { get; set; }
        public decimal? OrderScore { get; set; }
        public decimal? DistScore { get; set; }
        public decimal? StrikeScore { get; set; }
        public decimal? FinalScore { get; set; }
        public bool IsEligible { get; set; } = true;
        public string? IneligibleReason { get; set; }
        public bool IsFrozen { get; set; } = true;
        public string? ConfigSnapshot { get; set; }
        public string? Notes { get; set; }
        public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;
        public int? CalculatedBy { get; set; }
        public bool IsPublished { get; set; } = false;
        public DateTime? PublishedAt { get; set; }
        public int? PublishedBy { get; set; }
        public bool IsPaused { get; set; } = false;
        public DateTime? PausedAt { get; set; }
        public int? PausedBy { get; set; }
        public decimal? PrimAmount { get; set; }   // Hesaplanan prim tutarı (TL); hak edilmezse 0

        [ForeignKey("UserId")] public User? User { get; set; }
    }

    public class CalculationDetail
    {
        [Key] public long DetailId { get; set; }
        public int CalcId { get; set; }
        public int ProductId { get; set; }
        public decimal TargetQty { get; set; }
        public decimal ActualQty { get; set; }
        public decimal Rate { get; set; }
        public decimal CappedRate { get; set; }
        public decimal Score { get; set; }
        public bool IsCapped { get; set; } = false;

        [ForeignKey("CalcId")] public Calculation? Calculation { get; set; }
        [ForeignKey("ProductId")] public Product? Product { get; set; }
    }

    public class ImportLog
    {
        [Key] public int LogId { get; set; }
        public int UserId { get; set; }
        public string FileType { get; set; } = "";
        public string FileName { get; set; } = "";
        public long? FileSize { get; set; }
        public int? ImportRowCount { get; set; }
        public int? ReplacedCount { get; set; }
        public string Status { get; set; } = "Processing";
        public string? ErrorMsg { get; set; }
        public string? ValidationErrors { get; set; }
        public int? ProcessingTimeMs { get; set; }
        [MaxLength(64)] public string? FileHash { get; set; }   // SHA256 — çift yükleme tespiti
        public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("UserId")] public User? User { get; set; }
    }

    public class AuditLog
    {
        [Key] public long AuditId { get; set; }
        public int UserId { get; set; }
        public string Action { get; set; } = "";
        public string EntityType { get; set; } = "";
        public int? EntityId { get; set; }
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public string? IpAddress { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("UserId")] public User? User { get; set; }
    }

    public class Notification
    {
        [Key] public int NotifId { get; set; }
        public int UserId { get; set; }
        public string SenderType { get; set; } = "System";
        public int? SenderId { get; set; }
        public string Title { get; set; } = "";
        public string? Message { get; set; }
        public string Type { get; set; } = "";
        public bool IsRead { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("UserId")] public User? User { get; set; }
    }

    public class Event
    {
        [Key] public int EventId { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public DateTime EventDate { get; set; }
        public string EventType { get; set; } = "";
        public bool IsRecurring { get; set; } = false;
        public string TargetRole { get; set; } = "All";
        public int? TargetUserId { get; set; }
        public int CreatedBy { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class CalendarEntry
    {
        [Key] public int EntryId { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? MeetingLink { get; set; }
        public string? Location { get; set; }
        public bool IsAllDay { get; set; } = false;
        public string TargetRole { get; set; } = "All";
        public int CreatedBy { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class Announcement
    {
        [Key] public int AnnouncementId { get; set; }
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public string Category { get; set; } = "";
        public bool IsPinned { get; set; } = false;
        public string TargetRole { get; set; } = "All";
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public int CreatedBy { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class TaskItem
    {
        [Key] public int TaskId { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public int AssignedBy { get; set; }
        public int AssignedTo { get; set; }
        public DateTime? Deadline { get; set; }
        public string Priority { get; set; } = "Medium";
        public string Status { get; set; } = "New";
        public DateTime? CompletedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("AssignedBy")] public User? Assigner { get; set; }
        [ForeignKey("AssignedTo")] public User? Assignee { get; set; }
    }

    public class TaskComment
    {
        [Key] public int CommentId { get; set; }
        public int TaskId { get; set; }
        public int UserId { get; set; }
        public string Comment { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("TaskId")] public TaskItem? Task { get; set; }
        [ForeignKey("UserId")] public User? User { get; set; }
    }

    // ── Kullanıcı Oturum Takibi ──────────────────────────────────────────────
    public class UserSession
    {
        [Key] public int SessionId { get; set; }
        public int UserId { get; set; }
        public DateTime LoginAt { get; set; } = DateTime.UtcNow;
        public DateTime? LogoutAt { get; set; }
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
        public string? IpAddress { get; set; }
        public bool IsActive { get; set; } = true;

        [ForeignKey("UserId")] public User? User { get; set; }
    }

    // ── Yıldız Seçim Kriterleri ──────────────────────────────────────────────
    public class StarCriterion
    {
        [Key] public int Id { get; set; }
        public string StarKey { get; set; } = "";      // "week" | "month"
        public string Metric { get; set; } = "";       // "BoxQuantity" | "InstitutionCount" | "AmountTL" | "MFTL"
        public string DisplayLabel { get; set; } = ""; // UI'de gösterilecek metrik adı
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int UpdatedByUserId { get; set; }
    }

    // ── Prim Kademe Tablosu (skor → çarpan) ──────────────────────────────────
    public class PrimTier
    {
        [Key] public int TierId { get; set; }
        public decimal MinScore { get; set; }   // dahil alt sınır (ör. 0.85)
        public decimal MaxScore { get; set; }   // dahil üst sınır (ör. 0.95)
        public decimal Multiplier { get; set; } // taban tutarın çarpanı (ör. 1.0 = %100)
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int? UpdatedBy { get; set; }
    }

    // ── Rol Bazlı Prim Taban Tutarı ──────────────────────────────────────────
    public class RolePrimBase
    {
        [Key] public int Id { get; set; }
        public int RoleId { get; set; }
        public decimal BaseAmount { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int? UpdatedBy { get; set; }

        [ForeignKey("RoleId")] public Role? Role { get; set; }
    }

    // ── Genel Sistem Ayarı (anahtar-değer) ───────────────────────────────────
    public class SystemSetting
    {
        [Key] public int Id { get; set; }
        public string Key { get; set; } = "";    // ör. "SalesOnlyMode"
        public string Value { get; set; } = "";   // ör. "true" / "false"
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int? UpdatedBy { get; set; }
    }

    /// <summary>
    /// Import eşleştirmesinde hatırlanan kaynak-adı → kanonik varlık eşlemesi.
    /// Brick/ST adının kaynak dosyadaki varyantı (yazım farkı) bir kez onaylanınca burada
    /// saklanır; sonraki importlarda otomatik çözülür. AliasKey normalize edilmiş tutulur.
    /// </summary>
    public class NameAlias
    {
        [Key] public int NameAliasId { get; set; }
        [MaxLength(20)] public string AliasType { get; set; } = "";   // "Brick" | "User"
        [MaxLength(200)] public string AliasKey { get; set; } = "";   // Normalize edilmiş kaynak ad
        public int TargetId { get; set; }                              // BrickId / UserId
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int? CreatedBy { get; set; }
    }

    /// <summary>
    /// Yüklenen her dosyanın ham içeriği + meta'sı. "Yükleme Kütüphanesi": yükleme yıkıcı değil —
    /// dosya saklanır, geçmiş bir dosya tek tıkla yeniden aktifleştirilebilir (Replace/Append moduyla
    /// yeniden import edilir). Her FileType için en çok bir aktif batch (o an yüklü olan).
    /// </summary>
    public class ImportBatch
    {
        [Key] public int BatchId { get; set; }
        [MaxLength(20)] public string FileType { get; set; } = "";    // sellout|order|target|ruta|visitplan|productmap|brickmap
        [MaxLength(260)] public string FileName { get; set; } = "";
        [MaxLength(64)] public string? FileHash { get; set; }
        public byte[] Content { get; set; } = Array.Empty<byte>();   // ham dosya (yeniden yükleme için)
        [MaxLength(10)] public string Mode { get; set; } = "Replace"; // Replace | Append
        public int? Year { get; set; }
        [MaxLength(4)] public string? Quarter { get; set; }
        public bool IsActive { get; set; }
        public int? RowCount { get; set; }
        public decimal? QtyTotal { get; set; }
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
        public int? UploadedById { get; set; }
        [MaxLength(20)] public string Status { get; set; } = "Active";
    }

    // ── Saha Fotoğrafı (ST yükler, Admin/BM kontrol eder) ────────────────────
    public class StPhoto
    {
        [Key] public int Id { get; set; }
        public int UserId { get; set; }                  // yükleyen ST
        public string Title { get; set; } = "";
        public string? Purpose { get; set; }             // amaç/açıklama
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";    // image/png | image/jpeg | image/webp
        public long FileSize { get; set; }
        public byte[] ImageData { get; set; } = Array.Empty<byte>();
        public string Status { get; set; } = "Pending";  // Pending | Approved | Rejected
        public string? ReviewNote { get; set; }
        public int? ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey("UserId")] public User? User { get; set; }
        [ForeignKey("ReviewedBy")] public User? Reviewer { get; set; }
    }

    // ── Sipariş Leaderboard Yayın ────────────────────────────────────────────
    public class OrderLeaderboardPublish
    {
        [Key] public int PublishId { get; set; }
        public int? ProductId { get; set; }      // null = tüm ürünler
        public int? Year { get; set; }           // null = tüm zamanlar
        public string? Quarter { get; set; }     // null = tüm zamanlar
        public bool IsActive { get; set; } = true;
        public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
        public int PublishedBy { get; set; }
        public DateTime? StoppedAt { get; set; }
        public int? StoppedBy { get; set; }
        public bool IsPaused { get; set; } = false;
        public DateTime? PausedAt { get; set; }
        public int? PausedBy { get; set; }

        [ForeignKey("ProductId")] public Product? Product { get; set; }
        [ForeignKey("PublishedBy")] public User? Publisher { get; set; }
    }
}