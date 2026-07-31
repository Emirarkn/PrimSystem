/* ============================================================================
   PrimSystem — Brick→ST yeniden atama (2026-07, tam 3-ST haritası, 53 brick)
   ----------------------------------------------------------------------------
   Kullanıcının verdiği tam haritayı uygular:
     GÖKHAN ÇAYIRLI (16) → 16 brick
     SEVGİ  İNANÇ   (18) → 25 brick
     TARIK  KIZILKAYA (20) → 12 brick
   53 brick'in tamamı hâlihazırda 16/18/20 arasında; saf reshuffle.
   176/177/178 (ESKISEHIR BATI/GUNEY/KUZEY ILCELER) GÖKHAN'da KALIR — script
   bunlara ve diğer hiçbir brick/ST'ye DOKUNMAZ (yalnız aşağıdaki 53 brick).

   Idempotent + tek transaction. SSMS'te PrimDB üzerinde Execute.
   NOT: Önce db/userbrick-share.sql çalıştırılması önerilir (SharePercent kolonu);
        bu script o kolona REFERANS VERMEZ, yeni satırlar kolonun DEFAULT'unu (100) alır.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRAN;

DECLARE @m TABLE (BrickId INT PRIMARY KEY, NewUserId INT);
INSERT INTO @m (BrickId, NewUserId) VALUES
    -- GÖKHAN ÇAYIRLI (16)
    (35,16),(37,16),(38,16),(41,16),(45,16),(47,16),(49,16),(51,16),
    (56,16),(62,16),(67,16),(71,16),(179,16),(180,16),(181,16),(182,16),
    -- SEVGİ İNANÇ (18)
    (30,18),(31,18),(32,18),(33,18),(34,18),(39,18),(43,18),(44,18),(46,18),
    (50,18),(52,18),(53,18),(54,18),(55,18),(57,18),(63,18),(64,18),(66,18),
    (68,18),(69,18),(70,18),(107,18),(500,18),(501,18),(502,18),
    -- TARIK KIZILKAYA (20)
    (36,20),(40,20),(42,20),(60,20),(61,20),(65,20),(48,20),(59,20),(58,20),
    (365,20),(364,20),(359,20);

/* Güvenlik: 53 BrickId de mevcut mu? */
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

/* 2b) Yoksa ekle (SharePercent kolonu varsa DEFAULT 100 uygulanır) */
INSERT INTO dbo.UserBricks (UserId, BrickId, AssignedAt, IsActive)
SELECT m.NewUserId, m.BrickId, SYSUTCDATETIME(), 1
FROM @m m
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserBricks ub WHERE ub.UserId=m.NewUserId AND ub.BrickId=m.BrickId);

/* 3) Çakışma kontrolü: her taşınan brick tek aktif ST'de olmalı (bu harita ortak DEĞİL) */
IF EXISTS (
    SELECT ub.BrickId FROM dbo.UserBricks ub JOIN @m m ON m.BrickId=ub.BrickId
    WHERE ub.IsActive=1 GROUP BY ub.BrickId HAVING COUNT(*)>1)
BEGIN
    RAISERROR('Cakisma: bir brick birden fazla aktif ST de — geri alindi.',16,1);
    ROLLBACK TRAN; RETURN;
END

COMMIT TRAN;
PRINT 'Brick yeniden atama uygulandi (53 brick, 3 ST).';

/* Doğrulama — beklenen: 16=16, 18=25, 20=12 */
SELECT u.UserId, u.FullName,
   (SELECT COUNT(*) FROM dbo.UserBricks ub WHERE ub.UserId=u.UserId AND ub.IsActive=1) AS AktifBrick
FROM dbo.Users u WHERE u.UserId IN (16,18,20)
ORDER BY u.FullName;
