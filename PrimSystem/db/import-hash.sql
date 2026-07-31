/* ============================================================================
   PrimSystem — ImportLogs.FileHash kolonu (çift yükleme tespiti)
   ----------------------------------------------------------------------------
   IDEMPOTENT — tekrar çalıştırılabilir.
   Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -E -C -i import-hash.sql
   ============================================================================ */
SET NOCOUNT ON;

IF COL_LENGTH('dbo.ImportLogs', 'FileHash') IS NULL
BEGIN
    ALTER TABLE dbo.ImportLogs ADD FileHash NVARCHAR(64) NULL;
    PRINT 'ImportLogs.FileHash eklendi.';
END
ELSE
    PRINT 'ImportLogs.FileHash zaten mevcut.';

/* Çift yükleme sorgusu (FileType + FileHash) için indeks */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ImportLogs_Type_Hash' AND object_id=OBJECT_ID('dbo.ImportLogs'))
    CREATE INDEX IX_ImportLogs_Type_Hash ON dbo.ImportLogs(FileType, FileHash) INCLUDE(ImportedAt, Status);

PRINT 'import-hash.sql tamamlandı.';
