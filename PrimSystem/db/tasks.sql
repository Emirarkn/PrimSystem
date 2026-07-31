/* ============================================================================
   PrimSystem — Görev (Task) Modülü Şema Değişikliği
   ----------------------------------------------------------------------------
   ÖNEMLİ: Görev atama/takip ekranları bu DDL uygulanmadan ÇALIŞMAZ.
   TaskService tabloyu okur; tablo yoksa servis güvenli (boş/mesajlı) döner ama
   görev atanamaz/güncellenemez. Yeni sürümü dağıtmadan ÖNCE bu scripti çalıştırın.

   Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -i tasks.sql
            ya da SSMS'te PrimDB üzerinde açıp Execute.

   Tümü IDEMPOTENT'tir (varsa atlanır).
   ============================================================================ */

SET NOCOUNT ON;

/* 1) Tasks — BM → ST görevleri (tam yaşam döngüsü) */
IF OBJECT_ID('dbo.Tasks','U') IS NULL
BEGIN
    CREATE TABLE dbo.Tasks (
        TaskId      INT IDENTITY(1,1) PRIMARY KEY,
        Title       NVARCHAR(200) NOT NULL,
        Description NVARCHAR(MAX) NULL,
        AssignedBy  INT NOT NULL,                 -- atayan (BM / Admin / Yönetici)
        AssignedTo  INT NOT NULL,                 -- atanan ST
        Deadline    DATETIME2 NULL,
        Priority    NVARCHAR(20) NOT NULL CONSTRAINT DF_Tasks_Priority DEFAULT('Medium'),  -- Low | Medium | High
        Status      NVARCHAR(20) NOT NULL CONSTRAINT DF_Tasks_Status DEFAULT('New'),       -- New | InProgress | Completed | Rejected
        CompletedAt DATETIME2 NULL,
        CreatedAt   DATETIME2 NOT NULL CONSTRAINT DF_Tasks_CreatedAt DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_Tasks_Users_AssignedBy
            FOREIGN KEY (AssignedBy) REFERENCES dbo.Users(UserId),
        CONSTRAINT FK_Tasks_Users_AssignedTo
            FOREIGN KEY (AssignedTo) REFERENCES dbo.Users(UserId)
    );
    CREATE INDEX IX_Tasks_AssignedTo_Status ON dbo.Tasks(AssignedTo, Status);
    CREATE INDEX IX_Tasks_AssignedBy ON dbo.Tasks(AssignedBy);
END

/* 2) TaskComments — görev üzerindeki yazışma (atayan ↔ atanan) */
IF OBJECT_ID('dbo.TaskComments','U') IS NULL
BEGIN
    CREATE TABLE dbo.TaskComments (
        CommentId INT IDENTITY(1,1) PRIMARY KEY,
        TaskId    INT NOT NULL,
        UserId    INT NOT NULL,
        Comment   NVARCHAR(MAX) NOT NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_TaskComments_CreatedAt DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_TaskComments_Tasks_TaskId
            FOREIGN KEY (TaskId) REFERENCES dbo.Tasks(TaskId) ON DELETE CASCADE,
        CONSTRAINT FK_TaskComments_Users_UserId
            FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId)
    );
    CREATE INDEX IX_TaskComments_TaskId ON dbo.TaskComments(TaskId, CreatedAt);
END

PRINT 'Tasks + TaskComments şeması uygulandı (var olanlar atlandı).';
