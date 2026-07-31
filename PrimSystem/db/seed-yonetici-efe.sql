-- ============================================================================
-- seed-yonetici-efe.sql  (idempotent)
-- Efe Koçtürk — Yönetici (Kanal Müdürü, RoleId=2) hesabını oluşturur.
--
-- Giriş bilgileri:
--   Kullanıcı adı : EfeKo
--   Parola        : repoda TUTULMAZ (ilk girişte değiştirilmesi ZORUNLU — MustChangePassword=1)
--   PasswordHash  : ilk parolanın BCrypt hash'i (BCrypt.Net-Next) SQLCMD değişkeniyle verilir:
--                     sqlcmd -S localhost -d PrimDB -v PASSWORD_HASH='$2a$11$...' -i db/seed-yonetici-efe.sql
--                   SSMS'te: Query → SQLCMD Mode açık, en üste  :setvar PASSWORD_HASH "$2a$11$..."
--   Alternatif    : hash üretmek yerine hesap oluştuktan sonra Admin → Organizasyon →
--                   "şifre sıfırla" ile tek kullanımlık parola verin.
--
-- Yönetici tüm bölgeleri görür → RegionId = NULL, ManagerId = NULL.
-- Tekrar çalıştırılabilir: kullanıcı (EfeKo) zaten varsa hiçbir şey yapılmaz.
-- ============================================================================
SET NOCOUNT ON;

-- 0) Parola hash'i verilmiş mi? SQLCMD modu kapalıysa değişken çözülmez ve
--    literal kalır — bozuk hash'le hesap açmak yerine net hata ver.
IF N'$(PASSWORD_HASH)' NOT LIKE N'$2_$%'
BEGIN
    RAISERROR('PASSWORD_HASH SQLCMD degiskeni verilmedi (BCrypt hash bekleniyor). Dosya basligina bakin.', 16, 1);
    RETURN;
END

-- 1) Yönetici rolü (RoleId=2) mevcut mu? Yoksa net hata ver, devam etme.
IF NOT EXISTS (SELECT 1 FROM Roles WHERE RoleId = 2)
BEGIN
    RAISERROR('Roles tablosunda RoleId=2 (Yonetici) bulunamadi. Once roller tohumlanmali.', 16, 1);
    RETURN;
END

-- 2) Efe Koçtürk yoksa ekle (Username benzersizligi: EfeKo).
IF NOT EXISTS (SELECT 1 FROM Users WHERE Username = N'EfeKo')
BEGIN
    INSERT INTO Users
        (FullName, Username, Email, PasswordHash, RoleId, RegionId, ManagerId,
         HireDate, BirthDate, MinMonths, MustChangePassword, IsActive, IsDeleted, CreatedAt)
    VALUES
        (N'Efe Koçtürk',
         N'EfeKo',
         N'',
         N'$(PASSWORD_HASH)',  -- BCrypt(ilk parola) — SQLCMD değişkeni
         2,            -- RoleId = Yonetici (Kanal Müdürü)
         NULL,         -- RegionId — Yönetici tüm bölgeleri görür
         NULL,         -- ManagerId — üst yöneticisi yok
         CAST(SYSUTCDATETIME() AS date),  -- HireDate = bugün
         NULL,         -- BirthDate opsiyonel
         3,            -- MinMonths (varsayılan)
         1,            -- MustChangePassword = ilk girişte değiştir
         1,            -- IsActive
         0,            -- IsDeleted
         SYSUTCDATETIME());

    PRINT N'OK: Yönetici "Efe Koçtürk" (EfeKo) olusturuldu. Ilk parola giriste degisecek.';
END
ELSE
BEGIN
    PRINT N'Atlandi: "EfeKo" zaten mevcut, degisiklik yapilmadi.';
END
