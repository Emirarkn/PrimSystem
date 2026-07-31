# EF Core Migrations — Baseline ve Kullanım

Bu proje bugüne kadar şemayı **elle** yönetiyordu. Artık EF Core migration altyapısı
kuruldu. İlk migration `InitialBaseline` **mevcut modelin tamamını** (prim tabloları ve
`Calculation.PrimAmount` dahil) temsil eder.

- Migration kimliği: `20260601072226_InitialBaseline`
- EF Core sürümü: `8.0.13`
- Araç: `dotnet-ef` (local tool — `dotnet-tools.json`). Çağırma: `dotnet ef ...`

---

## ⚠️ MEVCUT (DOLU) VERİTABANI İÇİN — Baseline İşleme

`InitialBaseline.Up()` TÜM tabloları `CREATE TABLE` ile oluşturur. Mevcut PrimDB'de tablolar
zaten var; bu yüzden **`dotnet ef database update` ÇALIŞTIRMAYIN** — var olan tabloları
yeniden oluşturmaya çalışıp hata verir.

Bunun yerine migration'ı "uygulanmış" olarak işaretleyin (baseline). Önce şema güncel olsun:

1. (Henüz yapılmadıysa) prim şemasını ve indeksleri uygulayın:
   ```
   sqlcmd -S <sunucu> -d PrimDB -i prim-feature.sql
   sqlcmd -S <sunucu> -d PrimDB -i indexes.sql
   ```
2. Migration geçmiş tablosunu oluşturup baseline satırını ekleyin:
   ```sql
   IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
   CREATE TABLE [__EFMigrationsHistory] (
       [MigrationId]    nvarchar(150) NOT NULL,
       [ProductVersion] nvarchar(32)  NOT NULL,
       CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
   );

   IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260601072226_InitialBaseline')
   INSERT INTO [__EFMigrationsHistory] ([MigrationId],[ProductVersion])
   VALUES (N'20260601072226_InitialBaseline', N'8.0.13');
   ```

Artık EF, baseline'ı uygulanmış sayar ve yalnızca **bundan sonraki** değişiklikleri
(yeni migration'lar) uygular.

---

## BOŞ / YENİ VERİTABANI İÇİN

Sıfırdan bir DB kuruyorsanız baseline'a gerek yok; doğrudan:
```
dotnet ef database update --project PrimSystem/PrimSystem.csproj --startup-project PrimSystem/PrimSystem.csproj
```
Tüm tablolar oluşturulur. (Not: `prim-feature.sql`/`indexes.sql` çalıştırmayın — migration zaten kurar.)

---

## İLERİDE — Şema Değişikliği Akışı

1. Entity/DbContext'i değiştirin.
2. Migration üretin:
   ```
   dotnet ef migrations add <AnlamlıAd> --project PrimSystem/PrimSystem.csproj --startup-project PrimSystem/PrimSystem.csproj
   ```
3. **Üretilen `Up()`/`Down()`'u gözden geçirin** (özellikle ilk gerçek migration, model ile
   elle yönetilen DB arasındaki küçük farkları içerebilir — aşağıdaki nota bakın).
4. Uygulama:
   - Doğrudan: `dotnet ef database update ...`
   - Veya prod için idempotent script üretip DBA ile uygulayın:
     ```
     dotnet ef migrations script --idempotent -o migrate.sql --project PrimSystem/PrimSystem.csproj --startup-project PrimSystem/PrimSystem.csproj
     ```

Migration'ı geri almak (henüz uygulanmadıysa): `dotnet ef migrations remove ...`

---

## Bilinen Notlar / Dikkat

- **`Period.Quarter` hesaplanan (computed) kolon:** Modelde computed olarak işaretli ama SQL
  formülü modelde tanımlı değil. Baseline'da düz kolon olarak görünebilir; mevcut DB'de
  computed olarak kalır. Baseline işlerken `Up()` çalıştırılmadığı için sorun olmaz; ileride
  bu kolonu etkileyen migration üretilirse elle gözden geçirin.
- **Global query filter uyarıları:** `User` üzerinde `!IsDeleted` query filter var ve birçok
  entity (Calculation, Target, AuditLog, ...) User'a zorunlu FK ile bağlı. EF, "required end
  of relationship filtrelenebilir" uyarısı verir. Soft-delete edilmiş bir kullanıcının bağımlı
  kayıtları join'lerde beklenmedik biçimde elenebilir. İleride istenirse bağımlı entity'lere de
  eşleşen query filter eklenebilir veya ilişki opsiyonel yapılabilir.
- İlk gerçek migration'ı uygulamadan **mutlaka** gözden geçirin; elle yönetilen şema ile model
  snapshot'ı arasındaki tarihsel farklar burada yüzeye çıkabilir.
