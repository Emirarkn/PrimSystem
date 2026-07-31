# PrimSystem

Saha satış ekibi (ST / Satış Temsilcisi) için **çeyreklik prim hesaplama, performans skorlama ve saha analitiği** platformu. Farmasötik/OTC dağıtım modeline göre kurulmuş; brick (bölge kırılımı) bazlı hedef–gerçekleşen karşılaştırması yapar, 4 KPI'lı ağırlıklı skor üretir, skoru kademe tablosundan geçirerek TL prim tutarına çevirir.

- **Platform:** .NET 8 · Blazor Server (Interactive Server render mode)
- **Veritabanı:** SQL Server (EF Core 8.0.13, DbContextFactory deseni)
- **Kod büyüklüğü:** ~22.400 satır (uygulama ~21.100 + test ~1.270), 47 Razor bileşeni, 12 servis, 40 entity
- **Dil:** Arayüz ve kod yorumları Türkçe

---

## 1. Ne yapar?

| Aşama | İçerik |
|---|---|
| **Veri girişi** | 8 farklı Excel şablonu (Sell-Out, Sipariş, Hedef, Ruta, Ziyaret Planı, Eczane Planı, Ürün-Marka master, Brick atama) dry-run önizleme + kalıcı yükleme ile içe aktarılır |
| **Normalizasyon** | Türkçe İ/ı sorununu çözen kanonik anahtar, Levenshtein tabanlı eşleşme önerisi, kalıcı takma ad (NameAlias) hafızası |
| **Skorlama** | Ürün ağırlıklı satış skoru + Ruta uyum + Dağıtım + Strike Rate → ağırlıklı final skor |
| **Prim** | Final skor ≥ %85 gate → kademe çarpanı × rol bazlı taban tutar = TL prim |
| **Yayın** | Admin hesaplar → inceler → yayınlar; ST/BM ancak yayınlandıktan sonra görür (beklet/devam et desteği) |
| **Analitik** | Bölge / brick / eczane (GLN) / ürün kırılımlı raporlar, sipariş leaderboard'u, yıldız badge'leri, Excel dışa aktarım |
| **Yan modüller** | Görev atama (BM→ST), saha fotoğrafı onay akışı, bildirim merkezi, oturum takibi, denetim kaydı (audit log) |

---

## 2. Skor modeli

```
Ürün skoru      = min(gerçekleşen / hedef, 1.05) × ürün_ağırlığı / 100
Satış skoru     = Σ ürün skorları
Ruta skoru      = RutaUyum.Rate (import edilen oran)
Dağıtım skoru   = min(satış yapılan brick / atanan brick, 1.00)
Strike skoru    = min(sipariş alınan planlı GLN / planlı GLN, 1.00)   [plan yoksa null]

Final skor = 0.60·Satış + 0.15·Ruta + 0.15·Dağıtım + 0.10·Strike     (ağırlıklar KPICriteria'dan)
Gate       = Final skor ≥ 0.85
Prim (TL)  = eligible && gate ? RolePrimBase × PrimTier.Multiplier : 0
```

Ek kurallar:

- **%105 cap** — hedef aşımı ödüllendirilmez, iade (negatif) skoru eksiye düşürmez (0'a tabanlanır).
- **Kıdem eşiği** — `MonthsWorked(hireDate, quarterStart) < User.MinMonths` (varsayılan 3 ay) ise `IsEligible = false`, prim 0.
- **Run-rate modu** (opsiyonel, `SystemSettings.RunRateMode`) — sell-out verisi olan dönem sayısına göre hedefi orantılar; 3 aylık hedefi 2 aylık satışla kıyaslama adaletsizliğini giderir.
- **Ortak brick** — `UserBricks.SharePercent` ile hedef/sell-out/sipariş atıfı paylaştırılır (tek sahip = 100).

---

> **Dikkat:** `ScorecardWeight.MinRate` ve `KpiCriteria.MinRate` alanları kaydediliyor ve ekranda gösteriliyor ancak **skor hesabında kullanılmıyor**. Ayrıca ürün ağırlıklarının toplamı 100 değilse yalnız görsel uyarı verilir, kayıt engellenmez; KPI ağırlıkları için hiçbir toplam kontrolü yoktur.

---

## 2.1 Satış Temsilcisi (ST) Deneyimi

ST kendi hesabıyla girer ve **yalnızca kendi verisini** görür (`specificUserId` filtresi + `publishedOnly: true`). Yayınlanmamış hesaplama ST'ye hiç ulaşmaz.

Ana sayfada tek kişisel kart açılır:

- **ScoreGauge** — final skor halkası, üzerinde %85 gate çentiği, 0–105 ölçeğinde
- **KpiBreakdownBars** — 4 KPI'nın ayrı barları, her birinin ağırlık etiketiyle; veri yoksa soluk "—"
- **Prim kartı** — hak edilen tutar (₺) veya gerekçe ("Eşik aşılamadı" / "Prim dışı dönem" / "Tanımlı kademe yok")
- **Ürün detayı** — ürün başına `Hdf / Grc / %oran`, cap uygulandıysa `↑` işareti
- **Radar grafiği** — 4 KPI + kesikli %85 eşik çemberi (Chart.js)

`/reports` ve `/eczaneler` sayfalarında ise "Brick Performansım" ve "Bricklerimdeki En İyi Eczaneler" panelleri kendi kapsamına daraltılır. ST ayrıca görev durumu günceller, saha fotoğrafı yükler ve bildirimlerini takip eder.

---

## 2.2 Web Tabanlı Çalışma

Uygulama **sunucuda çalışır, tarayıcıdan erişilir** (Blazor Server + SignalR). C# kodu istemciye inmez; prim formülü ve sorgular sunucuda kalır. Her kullanıcı için sunucuda bir circuit tutulur.

Canlıya alma gereksinimleri:

| Gereksinim | Not |
|---|---|
| Windows Server + IIS (ASP.NET Core Hosting Bundle) veya Linux + Kestrel/nginx | |
| **WebSocket açık** reverse proxy | SignalR circuit'i için zorunlu |
| **Sticky session** | `SessionStore` ve `LoginThrottle` bellek içi — çok sunuculu kurulumda şart |
| HTTPS sertifikası | prod'da HSTS + HTTPS yönlendirme aktif |
| Environment variable'lar | `ConnectionStrings__DefaultConnection`, `Smtp__Password`, `AppBaseUrl`, `AllowedHosts` |
| SQL Server erişimi | uygulama hesabı bugün **DDL yetkisi** de istiyor (başlangıç self-heal bloğu) |
| Yazılabilir disk | `logs/` (30 gün) + DataProtection anahtar klasörü |
| İnternet erişimi (istemci) | **Chart.js CDN'den yükleniyor** — kapalı ağda grafikler sessizce boş kalır |

---

## 3. Roller

| Rol | RoleId | Görebildiği |
|---|---|---|
| **Admin** | 1 | Her şey: import, hesaplama, yayın, scorecard, prim ayarları, brick yönetimi, kontrol paneli, impersonation |
| **Yonetici** (Kanal Müdürü) | 2 | Tüm bölgeler — Overview drill-down, organizasyon, ham hesaplama dışa aktarımı |
| **BM** (Bölge Müdürü) | 3 | Kendi bölgesi — Bölge Analizi, görev atama, foto kontrol |
| **ST** (Satış Temsilcisi) | 4 | Kendi sonuçları + kendi brick'lerindeki eczaneler, görevleri, fotoğraf yükleme |

Yetki iki katmanda:

1. **UI guard'ları** — Razor sayfalarında `SessionState` (`IsAdmin`, `CanManage`, `CanViewAll`) ile `LoginRequired` / `NotAuthorizedView` gösterimi.
2. **Servis katmanı** — her çağrıda **DB'den taze** rol doğrulaması: `ImportService` 16, `CalculationService` 14, `AuthService` 2 metotta `EnsureAdminAsync`; `ExportService` `EnsureCanExportAllAsync` (Admin/Yonetici); `PhotoService` `CanReviewAsync` (Admin veya ilgili bölge BM'si); `TaskService` BM–ST ilişki kontrolü; `SessionState` impersonation'da admin'e bürünme yasağı.

> Not: Bu doğrulamalar **yazma** işlemlerini kapsar. **Okuma kapsamı** (hangi rolün hangi kayıtları göreceği) servis imzasında değil, çağıran sayfanın geçirdiği filtre parametresinde belirlenir.

Kimlik doğrulama ASP.NET Core Identity kullanmaz; BCrypt + `SessionStore` + `ProtectedSessionStorage` ile kendi hafif katmanını kurar. `UserSessions` tablosu giriş/çıkış/IP/son görülme bilgisini tutar.

---

## 4. Kurulum

### Gereksinimler
- .NET 8 SDK
- SQL Server (LocalDB / Express / tam sürüm)
- `dotnet-ef` 10.0.8 (local tool — `dotnet tool restore`)

### Adımlar

```bash
# 1) Bağımlılıklar
dotnet tool restore
dotnet restore

# 2) Bağlantı dizesi (geliştirme)
cd PrimSystem
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=PrimDB;Trusted_Connection=True;TrustServerCertificate=True;"
dotnet user-secrets set "Smtp:Password" "SMTP_PAROLASI"

# 3) Şema
#    BOŞ veritabanı  → migration'ları uygula:
dotnet ef database update
#    DOLU veritabanı → db/MIGRATIONS.md'deki baseline yöntemini izle (database update ÇALIŞTIRMA)

# 4) İndeksler ve ek şema scriptleri (idempotent)
sqlcmd -S localhost -d PrimDB -i db/indexes.sql

# 5) Çalıştır
dotnet run
```

Üretimde sırlar environment variable ile verilir (`Smtp__Password`, `ConnectionStrings__DefaultConnection`, `AppBaseUrl`, `AllowedHosts`) — ayrıntı: `PrimSystem/SECURITY-SETUP.md`.

### Testler

```bash
dotnet test PrimSystem.Tests/PrimSystem.Tests.csproj
```

> Not: `PrimSystem.slnx` yalnızca ana projeyi içerir; test projesi çözüme ekli değildir, bu yüzden yol açıkça verilmelidir.

---

## 5. Klasör yapısı

```
PrimSystem.slnx
PrimSystem/
  Program.cs                  # DI, Serilog, DataProtection, şema self-heal, güvenlik header'ları
  Data/
    Entities.cs               # 40 entity
    AppDbContext.cs           # DbSet'ler, precision, indeks, cascade kuralları, soft-delete filtresi
    RoleConstants.cs
  Services/
    ScoreCalculator.cs        # SAF skor matematiği (test edilebilir çekirdek)
    CalculationService.cs     # hesaplama + yayın + tüm analitik sorgular (2.107 satır)
    ImportService.cs          # 8 Excel importu + eşleştirme + rollback (2.492 satır)
    ExportService.cs          # EPPlus ile biçimli Excel çıktıları
    AuthService.cs / SessionState.cs / SessionStore.cs / LoginThrottle.cs
    TaskService.cs / PhotoService.cs / NotificationService.cs / AuditService.cs / EmailService.cs
  Components/
    Pages/                    # 23 sayfa (rota bazlı)
    Shared/                   # 18 yeniden kullanılabilir bileşen
    Layout/
  Migrations/                 # 11 EF migration (InitialBaseline + artımlılar)
  db/                         # 24 elle çalıştırılan SQL script + MIGRATIONS.md
  wwwroot/                    # app.css, fileUpload.js
PrimSystem.Tests/             # 111 xUnit testi (skor, import doğrulama, export, auth, task)
```

---

## 6. Rotalar

| Rota | Sayfa | Erişim |
|---|---|---|
| `/` | Ana Sayfa / Dashboard | Tüm roller (içerik role göre) |
| `/reports` | Raporlar | Tüm roller (kapsam role göre daraltılır) |
| `/eczaneler` | Eczane Analizi | Tüm roller (kapsam daraltılır) |
| `/notifications` | Bildirimler | Tüm roller |
| `/bolgem` | Bölge Analizi | BM |
| `/gorevler` | Görevler | BM, ST |
| `/overview` | Türkiye Overview | Yonetici, Admin |
| `/organization` | Organizasyon | Yonetici, Admin |
| `/photos` | Fotoğraf Yükle | ST |
| `/photo-review` | Foto Kontrol | Admin, BM |
| `/calculate` · `/import` · `/periods` · `/scorecard` · `/prim-config` · `/bricks` · `/panel` · `/activity` | Yönetim | Admin |
| `/login` · `/forgot-password` · `/reset-password` · `/change-password` | Kimlik | — |

---

## 7. Güvenlik notları

- Parolalar **BCrypt** ile hash'lenir; ilk girişte `MustChangePassword` zorunlu.
- Parola sıfırlama token'ı DB'de **SHA256 hash** olarak, 1 saat geçerli.
- Brute-force: kullanıcı adı bazlı 15 dakikada 5 hatalı deneme → 15 dakika kilit (`LoginThrottle`, bellek içi).
- Oturum: `ProtectedSessionStorage` içindeki token + bellek içi `SessionStore` (8 saat idle timeout); DataProtection anahtarları diske kalıcı yazılır.
- Tüm raw SQL **parametreli**; dinamik parçalar (metrik, plan yüklemi) whitelist `switch` ifadelerinden gelir.
- Güvenlik header'ları: `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`. CSP bilinçli olarak eklenmemiş (Blazor Server inline script gereksinimi).
- Denetim: **28 farklı aksiyon** `AuditLogs`'a yazılır (parola işlemleri, import/rollback, yayın, sıralama, yapılandırma, görev, foto, impersonation başlangıç/bitiş).

> **Denetim boşlukları:** `Calculate` (hesaplama çalıştırma), `Login`/`Logout`/`LoginFailed` ve kullanıcı CRUD aksiyonları audit'e yazılmıyor. `IpAddress` alanı mevcut ama beslenmiyor. Denetim kayıtlarını görüntüleyecek bir arayüz sayfası yok.

### Test durumu

**111 xUnit testi** (1.272 satır): skor matematiği 41 · import doğrulama 46 · export 9 · güvenlik 8 · görev 7.

Test edilmeyenler: `CalculateQuarterAsync` orkestrasyonu, import akışlarının kendisi, DB entegrasyonu, UI bileşenleri, **yetki kapsamı** ("ST başkasının primini göremez" kuralı test edilmiyor), yük/performans. CI yok; `coverlet` ekli ama kapsam raporlanmıyor.

---

## 8. Bilinen kısıtlar

- **EPPlus `LicenseContext.NonCommercial`** — ticari kullanımda lisans gerekir.
- **Bellek içi oturum/throttle** — tek sunucu varsayımı; yatay ölçeklemede sticky session veya dağıtık cache gerekir.
- **Yeniden hesaplama geçmişi silmez ama üzerine yazar** — bir çeyrek yeniden hesaplandığında o çeyreğin `Calculations` satırları silinip yeniden oluşturulur ve yayın durumu sıfırlanır. Yayınlanmış prim tutarının önceki hâli saklanmaz.
- **Canlı brick haritası** — hedef/gerçekleşen atıfı `UserBricks`'in *o anki* hâline göre yapılır; atama değişince geçmiş çeyreğin sonucu da değişebilir. `CalcFreshnessBanner` bunu uyarır ama otomatik dondurma yoktur.
- **Şema ikiliği** — EF migration'ları ile `db/*.sql` scriptleri paralel yürür; `Program.cs` içinde kolon eksikliğini kapatan bir "self-heal" bloğu vardır.

Ayrıntılı değerlendirme, mimari analiz ve iyileştirme yol haritası: **`PROJE-RAPORU.md`**
