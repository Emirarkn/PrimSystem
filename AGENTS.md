# AGENTS.md — PrimSystem

Bu dosya, repoda çalışan kodlama ajanları (Claude Code, Codex vb.) ve yeni geliştiriciler içindir.
Ürünün ne yaptığı: [README.md](README.md). Mimari analiz, bilinen riskler ve yol haritası:
[PROJE-RAPORU.md](PROJE-RAPORU.md). Bir göreve başlamadan önce ilgili bölümü okuyun.

PrimSystem, saha satış ekibinin **çeyreklik primini (TL) hesaplayan** bir .NET 8 Blazor Server
uygulamasıdır. Buradaki hatalar insanların maaşına yansır — doğruluk, hızdan önce gelir.

## Komutlar

```bash
dotnet build PrimSystem.slnx
dotnet test  PrimSystem.slnx          # 135 xUnit testi — her PR'dan önce yeşil olmalı
dotnet run --project PrimSystem       # yerel DB bağlantısı user-secrets'tan okunur

# Şema değişikliği (EF Core migration; dotnet-ef local tool → önce `dotnet tool restore`)
dotnet ef migrations add <AnlamliAd> --project PrimSystem/PrimSystem.csproj --startup-project PrimSystem/PrimSystem.csproj
```

`slnx` biçimi .NET SDK 9.0.200+ gerektirir (makinede SDK 10 kurulu; hedef framework net8.0).

## Mimari kurallar

- **DbContext'i asla enjekte etmeyin.** `IDbContextFactory<AppDbContext>` alın, her işlemde
  `using var db = _factory.CreateDbContext();`. Blazor Server'da eşzamanlı render'lar aynı
  context'i paylaşamaz.
- **Skor matematiği `ScoreCalculator`'da kalır** — saf, `static`, DB'siz. Yeni skor kuralı önce
  orada test edilebilir fonksiyon olarak yazılır, sonra `CalculationService` onu çağırır.
- **Yetki servis katmanında doğrulanır, UI gizlemesine güvenilmez.** Durum değiştiren her servis
  metodu `actorUserId` alır ve rolü DB'den taze okur (`EnsureAdminAsync` deseni). Yetki kontrolünü
  atlatan opsiyonel parametre (ör. `stoppedBy = 0`) eklemeyin.
- **Razor sayfalarına veri erişimi yazmayın.** İş mantığı ve DB yazımı servise gider.
  (`Organization.razor` bu kuralı çiğneyen bilinen bir istisnadır; düzeltilecek, örnek alınmamalı.)
- **Durum değiştiren işlemler denetlenir:** `AuditService.LogAsync(userId, action, entityType, ...)`.
- **Ham SQL yalnızca parametreli.** Dinamik parçalar (metrik, filtre) yalnızca sabit metinlerden
  seçilir; kullanıcı girdisi asla string'e eklenmez.
- **Türkçe metin karşılaştırması:** kültüre duyarlı `ToLower()/ToUpper()` kullanmayın (tr-TR'de
  `I → ı`). Eşleştirme anahtarları için `ImportService.Normalize`, diğerleri için `*Invariant`.
- **Kullanıcıya ham exception mesajı göstermeyin.** Ayrıntı `ILogger`'a, ekrana genel mesaj.
- **Yorumlar Türkçe ve "neden" odaklıdır** — neyi değil, hangi hatayı önlediğini anlatır.
  Çevredeki kodun yorum yoğunluğuna ve adlandırmasına uyun.

## Şema

- Yeni şema değişiklikleri **yalnızca EF migration** ile yapılır.
- `PrimSystem/db/*.sql` eski, elle çalıştırılan scriptlerdir; yenisini eklemeyin.
- `Program.cs` içindeki başlangıç "self-heal" `ALTER TABLE` bloğunu genişletmeyin (uygulama
  hesabına DDL yetkisi gerektiriyor; kaldırılması planlanıyor).
- Dolu bir veritabanında `dotnet ef database update` çalıştırmadan önce
  [PrimSystem/db/MIGRATIONS.md](PrimSystem/db/MIGRATIONS.md)'yi okuyun.

## Kırmızı çizgiler

Aşağıdakiler **board (insan) onayı** olmadan yapılmaz — önce issue'da öneri olarak yazın:

1. Prim/skor formülüne dokunan her değişiklik: `ScoreCalculator`, KPI ağırlıkları, prim kademeleri,
   gate eşiği, cap kuralları, `CalculationService.CalculateQuarterAsync`.
2. Kimlik doğrulama ve oturum: `AuthService`, `SessionState`, `SessionStore`, `LoginThrottle`
   (değişiklik serbest, ama PR'ı güvenlik incelemesine gönderin).
3. Yayınlanmış hesaplama verisini silen ya da üzerine yazan her işlem.

Asla yapılmayanlar:

- `main`'e doğrudan push, force-push, geçmişi yeniden yazma.
- Gerçek veri commit'lemek: `*.xlsx`, `*.csv`, çalışan adı/maaşı içeren dosyalar (`.gitignore`
  bunları zaten dışlar — dışlamayı kaldırmayın).
- Sır commit'lemek: parola, bağlantı dizesi, SMTP bilgisi, düz metin ilk parola. Sırlar
  user-secrets (geliştirme) veya environment variable (üretim) ile verilir.
- Üretim veritabanına bağlanmak. Ajanlar yalnızca yerel geliştirme veritabanını kullanır.

## Git akışı

- Her issue ayrı branch: `paperclip/<issue-id>-<kisa-ad>` (insanlar için `feature/...`).
- Bir PR tek bir konu. Açıklamada: ne değişti, neden, nasıl test edildi, hangi riskler kaldı.
- `dotnet build` uyarısız, `dotnet test` yeşil olmadan PR açılmaz. Yeni davranış yeni test ister;
  özellikle yetki kapsamı ("ST başkasının primini göremez") testsiz bırakılmaz.
- Birleştirmeyi (merge) insan yapar.

## Ortam notları

- Repo OneDrive altındaysa worktree ve `bin/obj` senkronizasyonu sorun çıkarır; ajan çalışmaları
  için OneDrive dışında bir klon kullanın (ör. `C:\dev\PrimSystem`).
- `PrimSystem/wwwroot/lib/` altındaki dosyalar vendored'dır; satır sonu dönüştürülmez,
  elle düzenlenmez (`chart.umd.min.js` SHA-384'ü `App.razor`'da kayıtlı).
