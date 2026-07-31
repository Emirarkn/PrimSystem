/* ============================================================================
   PrimSystem — Brick→ST yeniden atama (N_St_Brick haritası, 9 brick)
   ----------------------------------------------------------------------------
   N_St_Brick referans haritasıyla PrimSystem arasındaki GERÇEK farkı (9 brick)
   düzeltir. İSMAİL'in 6 Antalya brick'i KASITLI hariç (kullanıcı kararı).
   Yeni ST eklenmez; hepsi mevcut ST'ler arası taşıma.

   Taşımalar:
     ANK MAMAK/MAMAK GULVEREN/CEBECI (TARIK 20) -> GÖKHAN (16)
     ANK KIZILAY/KOCATEPE/SAGLIK     (SEVGİ 18) -> GÖKHAN (16)
     ANK GOLBASI/OVECLER             (TARIK 20) -> SEVGİ  (18)
     IST CEVIZLI                     (VOLKAN 27)-> TUNCAY (26)

   Idempotent + tek transaction. SSMS'te PrimDB üzerinde Execute.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRAN;

DECLARE @m TABLE (BrickId INT PRIMARY KEY, NewUserId INT);
INSERT INTO @m (BrickId, NewUserId) VALUES
    (58,16),(59,16),(40,16),(53,16),(54,16),(63,16),  -- GÖKHAN ÇAYIRLI
    (48,18),(61,18),                                   -- SEVGİ İNANÇ
    (243,26);                                          -- TUNCAY KETMEN

/* Güvenlik: 9 BrickId de mevcut mu? */
IF (SELECT COUNT(*) FROM @m m WHERE NOT EXISTS (SELECT 1 FROM dbo.Bricks b WHERE b.BrickId=m.BrickId)) > 0
BEGIN
    RAISERROR('Eslesmeyen BrickId var — durduruldu.',16,1); ROLLBACK TRAN; RETURN;
END

/* 1) Eski aktif sahipleri pasifle (yeni sahip hariç) */
UPDATE ub SET ub.IsActive=0, ub.UnassignedAt=SYSUTCDATETIME()
FROM dbo.UserBricks ub
JOIN @m m ON m.BrickId=ub.BrickId
WHERE ub.IsActive=1 AND ub.UserId<>m.NewUserId;

/* 2a) Yeni sahip kaydı varsa aktive et */
UPDATE ub SET ub.IsActive=1, ub.AssignedAt=SYSUTCDATETIME(), ub.UnassignedAt=NULL
FROM dbo.UserBricks ub
JOIN @m m ON m.BrickId=ub.BrickId AND m.NewUserId=ub.UserId;

/* 2b) Yoksa ekle */
INSERT INTO dbo.UserBricks (UserId, BrickId, AssignedAt, IsActive)
SELECT m.NewUserId, m.BrickId, SYSUTCDATETIME(), 1
FROM @m m
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserBricks ub WHERE ub.UserId=m.NewUserId AND ub.BrickId=m.BrickId);

/* 3) Çakışma kontrolü: her taşınan brick tek aktif ST'de olmalı */
IF EXISTS (
    SELECT ub.BrickId FROM dbo.UserBricks ub JOIN @m m ON m.BrickId=ub.BrickId
    WHERE ub.IsActive=1 GROUP BY ub.BrickId HAVING COUNT(*)>1)
BEGIN
    RAISERROR('Cakisma: bir brick birden fazla aktif ST de — geri alindi.',16,1);
    ROLLBACK TRAN; RETURN;
END

COMMIT TRAN;
PRINT 'Brick yeniden atama uygulandi (9 brick).';

/* Doğrulama */
SELECT u.FullName,
   (SELECT COUNT(*) FROM dbo.UserBricks ub WHERE ub.UserId=u.UserId AND ub.IsActive=1) AS AktifBrick
FROM dbo.Users u WHERE u.UserId IN (16,18,20,26,27)
ORDER BY u.FullName;
