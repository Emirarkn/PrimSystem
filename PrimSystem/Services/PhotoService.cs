using Microsoft.EntityFrameworkCore;
using PrimSystem.Data;

namespace PrimSystem.Services
{
    /// <summary>
    /// Saha fotoğrafı modülü: ST yükler, Admin + ilgili bölge BM'si kontrol eder.
    /// Görüntü DB'de varbinary olarak saklanır (StPhotos tablosu — db/st-photos.sql).
    /// </summary>
    public class PhotoService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly AuditService _audit;
        private readonly ILogger<PhotoService> _logger;

        public const long MaxBytes = 5 * 1024 * 1024; // 5 MB
        private static readonly HashSet<string> _allowedTypes =
            new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/jpg", "image/webp" };

        public PhotoService(IDbContextFactory<AppDbContext> factory, AuditService audit, ILogger<PhotoService> logger)
        {
            _factory = factory;
            _audit = audit;
            _logger = logger;
        }

        public bool TableMissingMessageNeeded { get; private set; }

        private async Task<bool> TableExistsAsync(AppDbContext db)
        {
            try
            {
                var exists = await db.Database
                    .SqlQueryRaw<int>("SELECT CAST(CASE WHEN OBJECT_ID('dbo.StPhotos','U') IS NOT NULL THEN 1 ELSE 0 END AS int) AS Value")
                    .FirstAsync();
                return exists == 1;
            }
            catch { return false; }
        }

        // ── ST: yükleme ────────────────────────────────────────────────────────
        public async Task<(bool Ok, string? Error)> UploadAsync(
            int userId, string title, string? purpose, byte[] data, string contentType, string fileName)
        {
            if (string.IsNullOrWhiteSpace(title)) return (false, "Başlık zorunlu.");
            if (data == null || data.Length == 0) return (false, "Dosya boş.");
            if (data.Length > MaxBytes) return (false, "Dosya çok büyük (maks 5 MB).");
            if (!_allowedTypes.Contains(contentType)) return (false, "Sadece PNG, JPEG veya WEBP görseller kabul edilir.");

            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db))
                return (false, "Foto tablosu yok — db/st-photos.sql çalıştırılmalı.");

            db.StPhotos.Add(new StPhoto
            {
                UserId = userId,
                Title = title.Trim(),
                Purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim(),
                FileName = fileName,
                ContentType = contentType,
                FileSize = data.Length,
                ImageData = data,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            await _audit.LogAsync(userId, "UploadPhoto", "StPhoto", null, newValue: title);
            return (true, null);
        }

        // ── ST: kendi fotoları (görüntü hariç hafif) ─────────────────────────────
        public async Task<List<StPhotoListItem>> GetMyPhotosAsync(int userId)
        {
            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db)) return new();
            return await db.StPhotos
                .Where(p => p.UserId == userId)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new StPhotoListItem(
                    p.Id, p.UserId, p.User!.FullName, p.Title, p.Purpose,
                    p.Status, p.ReviewNote, p.ReviewedAt, p.CreatedAt, p.ContentType))
                .ToListAsync();
        }

        // ── Admin/BM: kontrol kuyruğu ────────────────────────────────────────────
        public async Task<List<StPhotoListItem>> GetForReviewAsync(int reviewerUserId)
        {
            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db)) return new();
            var reviewer = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == reviewerUserId);
            if (reviewer == null) return new();

            var q = db.StPhotos.AsQueryable();
            if (reviewer.RoleId == RoleId.BM)
            {
                // BM: yalnız kendi bölgesindeki ST'lerin fotoları.
                q = q.Where(p => p.User!.RegionId == reviewer.RegionId);
            }
            else if (reviewer.RoleId != RoleId.Admin)
            {
                return new(); // başka rol kontrol edemez
            }

            return await q
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new StPhotoListItem(
                    p.Id, p.UserId, p.User!.FullName, p.Title, p.Purpose,
                    p.Status, p.ReviewNote, p.ReviewedAt, p.CreatedAt, p.ContentType))
                .ToListAsync();
        }

        // ── Admin/BM: onayla / reddet ────────────────────────────────────────────
        public async Task<(bool Ok, string? Error)> ReviewAsync(int photoId, int reviewerUserId, bool approve, string? note)
        {
            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db)) return (false, "Foto tablosu yok — db/st-photos.sql çalıştırılmalı.");

            var photo = await db.StPhotos.Include(p => p.User).FirstOrDefaultAsync(p => p.Id == photoId);
            if (photo == null) return (false, "Fotoğraf bulunamadı.");
            if (!await CanReviewAsync(db, reviewerUserId, photo)) return (false, "Bu işlem için yetkiniz yok.");

            photo.Status = approve ? "Approved" : "Rejected";
            photo.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            photo.ReviewedBy = reviewerUserId;
            photo.ReviewedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await _audit.LogAsync(reviewerUserId, approve ? "ApprovePhoto" : "RejectPhoto", "StPhoto", photoId,
                newValue: $"{photo.Title} → {photo.Status}");
            return (true, null);
        }

        // ── Görüntü baytları (yetki kontrollü) ───────────────────────────────────
        public async Task<(byte[] Data, string ContentType)?> GetImageAsync(int photoId, int requesterUserId)
        {
            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db)) return null;
            var photo = await db.StPhotos.Include(p => p.User).FirstOrDefaultAsync(p => p.Id == photoId);
            if (photo == null) return null;
            if (photo.UserId != requesterUserId && !await CanReviewAsync(db, requesterUserId, photo)) return null;
            return (photo.ImageData, photo.ContentType);
        }

        private async Task<bool> CanReviewAsync(AppDbContext db, int reviewerUserId, StPhoto photo)
        {
            var reviewer = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == reviewerUserId);
            if (reviewer == null) return false;
            if (reviewer.RoleId == RoleId.Admin) return true;
            if (reviewer.RoleId == RoleId.BM)
                return photo.User?.RegionId != null && photo.User.RegionId == reviewer.RegionId;
            return false;
        }
    }

    public record StPhotoListItem(
        int Id,
        int UserId,
        string UploaderName,
        string Title,
        string? Purpose,
        string Status,
        string? ReviewNote,
        DateTime? ReviewedAt,
        DateTime CreatedAt,
        string ContentType);
}
