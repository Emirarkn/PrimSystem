using Microsoft.EntityFrameworkCore;
using PrimSystem.Data;

namespace PrimSystem.Services
{
    /// <summary>
    /// Görev (task) modülü: BM kendi bölgesindeki ST'lere görev atar; ST durum günceller + yorum yazar.
    /// Tablolar Tasks/TaskComments (db/tasks.sql). Yetki DB'den doğrulanır (UI guard'ına ek savunma —
    /// NotificationService.EnsureCanSendBulkAsync / PhotoService.CanReviewAsync deseni).
    /// Atama/güncelleme sonrası ilgili tarafa NotificationService ile bildirim düşer.
    /// </summary>
    public class TaskService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly NotificationService _notify;
        private readonly AuditService _audit;
        private readonly ILogger<TaskService> _logger;

        private static readonly HashSet<string> _priorities =
            new(StringComparer.OrdinalIgnoreCase) { "Low", "Medium", "High" };
        private static readonly HashSet<string> _stStatuses =
            new(StringComparer.OrdinalIgnoreCase) { "New", "InProgress", "Completed", "Rejected" };

        public TaskService(IDbContextFactory<AppDbContext> factory, NotificationService notify,
            AuditService audit, ILogger<TaskService> logger)
        {
            _factory = factory;
            _notify = notify;
            _audit = audit;
            _logger = logger;
        }

        private async Task<bool> TableExistsAsync(AppDbContext db)
        {
            try
            {
                var exists = await db.Database
                    .SqlQueryRaw<int>("SELECT CAST(CASE WHEN OBJECT_ID('dbo.Tasks','U') IS NOT NULL THEN 1 ELSE 0 END AS int) AS Value")
                    .FirstAsync();
                return exists == 1;
            }
            catch { return false; }
        }

        // ── BM/Admin: tek ST'ye görev ata ────────────────────────────────────────
        public async Task<(bool Ok, string? Error)> AssignAsync(
            int assignerId, int assignedToUserId, string title, string? description,
            DateTime? deadline, string priority)
        {
            if (string.IsNullOrWhiteSpace(title)) return (false, "Başlık zorunlu.");
            if (!_priorities.Contains(priority)) priority = "Medium";

            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db))
                return (false, "Görev tablosu yok — db/tasks.sql çalıştırılmalı.");

            var assigner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == assignerId);
            var assignee = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == assignedToUserId);
            if (assigner == null || assignee == null) return (false, "Kullanıcı bulunamadı.");
            if (!CanAssignTo(assigner, assignee)) return (false, "Bu ST'ye görev atama yetkiniz yok.");

            db.Tasks.Add(new TaskItem
            {
                Title = title.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                AssignedBy = assignerId,
                AssignedTo = assignedToUserId,
                Deadline = deadline,
                Priority = priority,
                Status = "New",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            await _audit.LogAsync(assignerId, "AssignTask", "TaskItem", null,
                newValue: $"{title} → {assignee.FullName}");

            // Bildirim (görev tipi). Bildirim hatası görev atamasını engellemez.
            try
            {
                await _notify.SendAsync(assignedToUserId, $"Yeni görev: {title.Trim()}",
                    string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    "Task", senderId: assignerId);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Görev bildirimi gönderilemedi (task assignee={Id})", assignedToUserId); }

            return (true, null);
        }

        // ── BM/Admin: bölgenin tüm ST'lerine toplu görev ata ─────────────────────
        public async Task<(bool Ok, string? Error, int Count)> AssignToTeamAsync(
            int assignerId, string title, string? description, DateTime? deadline, string priority)
        {
            if (string.IsNullOrWhiteSpace(title)) return (false, "Başlık zorunlu.", 0);
            if (!_priorities.Contains(priority)) priority = "Medium";

            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db))
                return (false, "Görev tablosu yok — db/tasks.sql çalıştırılmalı.", 0);

            var assigner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == assignerId);
            if (assigner == null) return (false, "Kullanıcı bulunamadı.", 0);
            if (assigner.RoleId is not (RoleId.Admin or RoleId.Yonetici or RoleId.BM))
                return (false, "Toplu görev atama yetkiniz yok.", 0);

            // BM → yalnız kendi bölgesi; Admin/Yönetici → bölge yoksa tüm aktif ST'ler.
            var stQuery = db.Users.Where(u => u.RoleId == RoleId.ST && u.IsActive && !u.IsDeleted);
            if (assigner.RoleId == RoleId.BM)
                stQuery = stQuery.Where(u => u.RegionId == assigner.RegionId);
            var targets = await stQuery.Select(u => u.UserId).ToListAsync();
            if (!targets.Any()) return (false, "Bölgenizde aktif ST bulunamadı.", 0);

            var now = DateTime.UtcNow;
            db.Tasks.AddRange(targets.Select(uid => new TaskItem
            {
                Title = title.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                AssignedBy = assignerId,
                AssignedTo = uid,
                Deadline = deadline,
                Priority = priority,
                Status = "New",
                CreatedAt = now
            }));
            await db.SaveChangesAsync();
            await _audit.LogAsync(assignerId, "AssignTeamTask", "TaskItem", null,
                newValue: $"{title} → {targets.Count} ST");

            try
            {
                await _notify.SendBulkAsync(targets, $"Yeni görev: {title.Trim()}",
                    string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    "Task", senderId: assignerId);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Toplu görev bildirimi gönderilemedi"); }

            return (true, null, targets.Count);
        }

        // ── ST: kendi görevleri ──────────────────────────────────────────────────
        public async Task<List<TaskListItem>> GetMyTasksAsync(int stUserId)
        {
            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db)) return new();
            return await db.Tasks
                .Where(t => t.AssignedTo == stUserId)
                .OrderBy(t => t.Status == "Completed" || t.Status == "Rejected" ? 1 : 0)
                .ThenByDescending(t => t.CreatedAt)
                .Select(t => new TaskListItem(
                    t.TaskId, t.Title, t.Description, t.AssignedBy, t.Assigner!.FullName,
                    t.AssignedTo, t.Assignee!.FullName, t.Deadline, t.Priority, t.Status,
                    t.CompletedAt, t.CreatedAt))
                .ToListAsync();
        }

        // ── BM/Admin: atadığım görevler (durum takibi) ───────────────────────────
        public async Task<List<TaskListItem>> GetAssignedByAsync(int assignerId, string? statusFilter = null)
        {
            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db)) return new();
            var q = db.Tasks.Where(t => t.AssignedBy == assignerId);
            if (!string.IsNullOrWhiteSpace(statusFilter) && statusFilter != "All")
                q = q.Where(t => t.Status == statusFilter);
            return await q
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new TaskListItem(
                    t.TaskId, t.Title, t.Description, t.AssignedBy, t.Assigner!.FullName,
                    t.AssignedTo, t.Assignee!.FullName, t.Deadline, t.Priority, t.Status,
                    t.CompletedAt, t.CreatedAt))
                .ToListAsync();
        }

        // ── ST: durum güncelle (yalnız atanan kişi kendi görevini) ───────────────
        public async Task<(bool Ok, string? Error)> UpdateStatusAsync(int taskId, int actorUserId, string status)
        {
            if (!_stStatuses.Contains(status)) return (false, "Geçersiz durum.");

            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db)) return (false, "Görev tablosu yok — db/tasks.sql çalıştırılmalı.");

            var task = await db.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
            if (task == null) return (false, "Görev bulunamadı.");
            if (task.AssignedTo != actorUserId) return (false, "Yalnız size atanan görevin durumunu değiştirebilirsiniz.");

            task.Status = status;
            task.CompletedAt = status == "Completed" ? DateTime.UtcNow : null;
            await db.SaveChangesAsync();
            await _audit.LogAsync(actorUserId, "UpdateTaskStatus", "TaskItem", taskId, newValue: status);

            // Atayanı bilgilendir.
            try
            {
                await _notify.SendAsync(task.AssignedBy, $"Görev güncellendi: {task.Title}",
                    $"Durum: {StatusLabel(status)}", "Task", senderId: actorUserId);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Görev durum bildirimi gönderilemedi (task={Id})", taskId); }

            return (true, null);
        }

        // ── Yorum ekle (yalnız atayan ↔ atanan) ──────────────────────────────────
        public async Task<(bool Ok, string? Error)> AddCommentAsync(int taskId, int userId, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return (false, "Yorum boş olamaz.");

            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db)) return (false, "Görev tablosu yok — db/tasks.sql çalıştırılmalı.");

            var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId);
            if (task == null) return (false, "Görev bulunamadı.");
            if (task.AssignedBy != userId && task.AssignedTo != userId)
                return (false, "Bu göreve yorum yazma yetkiniz yok.");

            db.TaskComments.Add(new TaskComment
            {
                TaskId = taskId, UserId = userId, Comment = text.Trim(), CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            // Karşı tarafa bildirim.
            var other = task.AssignedBy == userId ? task.AssignedTo : task.AssignedBy;
            try
            {
                await _notify.SendAsync(other, $"Görev yorumu: {task.Title}", text.Trim(), "Task", senderId: userId);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Görev yorum bildirimi gönderilemedi (task={Id})", taskId); }

            return (true, null);
        }

        public async Task<List<TaskCommentItem>> GetCommentsAsync(int taskId, int requesterUserId)
        {
            using var db = _factory.CreateDbContext();
            if (!await TableExistsAsync(db)) return new();
            var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId);
            if (task == null) return new();
            if (task.AssignedBy != requesterUserId && task.AssignedTo != requesterUserId)
                return new();
            return await db.TaskComments
                .Where(c => c.TaskId == taskId)
                .OrderBy(c => c.CreatedAt)
                .Select(c => new TaskCommentItem(c.CommentId, c.UserId, c.User!.FullName, c.Comment, c.CreatedAt))
                .ToListAsync();
        }

        // BM yalnız kendi bölgesindeki ST'ye; Admin/Yönetici herhangi bir aktif ST'ye.
        // Saf yetki kuralı (DB'siz test edilebilir).
        internal static bool CanAssignTo(User assigner, User assignee)
        {
            if (assignee.RoleId != RoleId.ST || !assignee.IsActive || assignee.IsDeleted)
                return false;
            if (assigner.RoleId == RoleId.Admin || assigner.RoleId == RoleId.Yonetici)
                return true;
            if (assigner.RoleId == RoleId.BM)
                return assignee.RegionId != null && assignee.RegionId == assigner.RegionId;
            return false;
        }

        private static string StatusLabel(string s) => s switch
        {
            "New" => "Yeni",
            "InProgress" => "Devam ediyor",
            "Completed" => "Tamamlandı",
            "Rejected" => "Reddedildi",
            _ => s
        };
    }

    public record TaskListItem(
        int TaskId, string Title, string? Description,
        int AssignedBy, string AssignerName,
        int AssignedTo, string AssigneeName,
        DateTime? Deadline, string Priority, string Status,
        DateTime? CompletedAt, DateTime CreatedAt);

    public record TaskCommentItem(int CommentId, int UserId, string UserName, string Comment, DateTime CreatedAt);
}
