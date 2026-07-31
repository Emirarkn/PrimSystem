# PrimSystem — Detaylı Proje Analiz Raporu

**Tarih:** 30 Temmuz 2026
**Kapsam:** `C:\Users\eamir\OneDrive\Belgeler\Çalışmalar\Çalışma\PrimSystem` — tüm kaynak kod, migration'lar, SQL scriptleri, testler ve yapılandırma
**Yöntem:** Statik kod incelemesi (kod çalıştırılmadı, hiçbir dosya değiştirilmedi)

---

## 1. Yönetici Özeti

PrimSystem, farmasötik/OTC saha satış organizasyonu için **çeyreklik prim hesaplama ve performans yönetim platformu**dur. Tek bir .NET 8 Blazor Server uygulaması olarak inşa edilmiş; SQL Server üzerinde 40 entity ile çalışıyor.

Sistemin işlevsel çekirdeği şu döngüdür:

> **Excel'den ham saha verisi → kanonik eşleştirme → brick bazlı hedef/gerçekleşen skorlama → 4 KPI ağırlıklı final skor → gate + kademe → TL prim → yönetici onaylı yayın → role göre kısıtlı raporlama**

**Genel değerlendirme:** Bu, "hobi projesi" seviyesinin belirgin şekilde üzerinde, üretimde kullanılan (log dosyaları, gerçek şirket verisi içeren Excel'ler, 11 migration, 2 aylık aktif geliştirme izi) bir iş uygulaması. Domain modeli olgun, iş kurallarının kenar durumları düşünülmüş ve kod yorumları istisnai kalitede — çoğu yorum "ne yaptığını" değil **"neden böyle yapıldığını ve hangi hatayı önlediğini"** açıklıyor. Bu, çoğu kurumsal kod tabanında görülmeyen bir olgunluk göstergesi.

Buna karşılık iki yapısal zayıflık öne çıkıyor: **(a)** iki dosya (`CalculationService` 2.107 satır, `ImportService` 2.492 satır) uygulamanın tüm iş mantığını taşıyor ve büyümeye devam ediyor; **(b)** hesaplama sonuçlarının değişmezliği (immutability) garanti edilmiyor — yeniden hesaplama, yayınlanmış prim geçmişini geri dönüşsüz olarak üzerine yazıyor. İkinci madde, para ödemesine dayanak olan bir sistemde en yüksek öncelikli risktir.

| Boyut | Değerlendirme |
|---|---|
| İş mantığı doğruluğu ve derinliği | Çok iyi |
| Kod içi dokümantasyon (yorumlar) | Örnek nitelikte |
| Güvenlik temelleri (auth, SQLi, parola) | İyi |
| Test kapsamı (saf mantık) | İyi — 111 test |
| Test kapsamı (DB/entegrasyon/UI) | Yok |
| Mimari ayrışma / modülerlik | Zayıf |
| Sunum katmanı bakımı (inline CSS) | Zayıf |
| Denetlenebilirlik / veri değişmezliği | Riskli |
| Ölçeklenebilirlik (yatay) | Kısıtlı |
| Operasyonel olgunluk (CI, git, dağıtım) | Eksik |

---

## 2. Ana Amaç ve İş Bağlamı

### 2.1 Çözdüğü problem

Saha satış primleri klasik olarak Excel'de hesaplanır. Bu yaklaşımın üç kronik sorunu var ve PrimSystem doğrudan bunları hedefliyor:

1. **Atıf tutarsızlığı** — Hedef dosyası bir mümessil-brick haritası, sell-out dosyası başka bir harita taşır; aynı çeyrekte "hedefi olan ama satışı başkasına yazılan" ST'ler oluşur. PrimSystem bunu, **hem hedefi hem gerçekleşeni aynı canlı `UserBricks` haritasından türeterek** çözüyor (`CalculationService.cs:349-360`'daki yorum bu kararı açıkça gerekçelendiriyor).
2. **Eşleştirme kaybı** — "İzmir/Izmir", "İTRİYAT/ITRIYAT", çift boşluk gibi farklar Excel'de sessizce sıfır satış üretir. `Normalize()` fonksiyonu Türkçe nokta sorununu (i/İ/ı/I → I) çözerken ş/ç/ğ/ü/ö'yü bilinçli olarak **katlamıyor** — yanlış birleşme riskine karşı. Bu, konuya vakıf bir tasarım kararı.
3. **Şeffaflık ve itiraz edilebilirlik** — Prim itirazlarında "neden bu skor" sorusunun cevabı gerekir. Sistem her hesaplamada ürün bazlı `CalculationDetail` (hedef, gerçekleşen, ham oran, cap'li oran, skor, cap bayrağı) ve o anki tüm konfigürasyonun JSON `ConfigSnapshot`'ını saklıyor.

### 2.2 Tasarım felsefesi

Kod tabanından okunabilen üç yönlendirici ilke:

- **"Sessiz hata olmaz."** Import'ta eşleşmeyen değerler sayılır ve raporlanır; dosya toplamı ile DB toplamı karşılaştırılıp `Verified` bayrağı üretilir (`AmountsVerified`, satır sayısıyla ölçeklenen tolerans); scorecard düzenlemesinde hiçbir alan değişmediyse kullanıcı uyarılır (`ScorecardEditIsNoOp`); ağırlık değişince "yeniden hesapla gerekli" bandı tetiklenir.
- **"Yıkıcı işlem olmaz."** Dry-run önizleme, SHA256 ile çift yükleme tespiti, ham dosyayı saklayan "Yükleme Kütüphanesi" (`ImportBatch.Content`), tam-eşleşmeli `ImportedAt` damgasıyla atomik rollback.
- **"Sunucu tarafı doğrulama, UI gizlemesine güvenmez."** Her kritik servis metodunda DB'den rol doğrulaması yapılıyor.

---

## 3. Mimari

### 3.1 Katmanlar

```
┌───────────────────────────────────────────────────────────────┐
│  Blazor Server — 23 sayfa + 18 paylaşılan bileşen             │
│  (InteractiveServer render mode, SignalR circuit)             │
└──────────────────────────┬────────────────────────────────────┘
                           │  DI ile enjekte edilen servisler
┌──────────────────────────▼────────────────────────────────────┐
│  SERVİS KATMANI (12 servis)                                    │
│  ScoreCalculator  ← saf, yan etkisiz, DB'siz (test çekirdeği)   │
│  CalculationService · ImportService · ExportService             │
│  AuthService · SessionState · SessionStore · LoginThrottle      │
│  TaskService · PhotoService · NotificationService               │
│  AuditService · EmailService                                    │
└──────────────────────────┬────────────────────────────────────┘
                           │  IDbContextFactory<AppDbContext>
┌──────────────────────────▼────────────────────────────────────┐
│  VERİ KATMANI — EF Core 8 + ham ADO.NET (ağır analitik)        │
│  SQL Server · 40 entity · 11 migration + 24 elle SQL script     │
└───────────────────────────────────────────────────────────────┘
```

### 3.2 Yerinde olan mimari kararlar

| Karar | Gerekçe |
|---|---|
| `AddDbContextFactory` (Scoped `DbContext` yerine) | Blazor Server'da eşzamanlı bileşen render'ları aynı `DbContext`'i paylaşamaz — bu, Blazor'da en sık yapılan hatanın doğru çözümü |
| `ScoreCalculator` ayrı, saf, `static` | Skor matematiği DB'den bağımsız test edilebiliyor; 41 test bu sınıfı hedefliyor |
| Ağır analitikte ham ADO.NET | LINQ'in üreteceği çok-JOIN'li sorgular yerine elle yazılmış, `OPTION (RECOMPILE)`'lı, kapsayıcı indekslere hizalanmış SQL; timeout'lar açıkça set edilmiş (30–120 sn) |
| DataProtection anahtarlarının diske kalıcı yazılması | Aksi halde her restart oturumları düşürür — `Program.cs:24-31` bunu yorumla gerekçelendiriyor |
| EF `Database.Command` log seviyesinin `Warning`'e çekilmesi | Her istekte yüzlerce SQL satırı loglanmasını engelliyor, komut hatalarını koruyor |
| Global soft-delete query filter (`User.!IsDeleted`) | Silinmiş kullanıcı her sorgudan otomatik düşüyor |
| `SellOut` (brick toplamı, skor için) ile `SellOutDetail` (GLN kırılımı, analiz için) ayrımı | Skorlama sorgusunu 27 bin eczane satırından korumak; ikisinin toplamı invariant olarak dokümante edilmiş |

### 3.3 Mimari zayıflıklar

**(a) Tanrı-servis (God Service) problemi.** `CalculationService` üç ayrı sorumluluğu taşıyor: prim hesaplama motoru, yayın/durum yönetimi (publish/pause/unpublish/leaderboard) ve **17 farklı analitik sorgu metodu** (bölge, brick, eczane, ürün matrisi, aktivite trendi, yıldız badge'leri...). Bu üçü farklı değişim hızlarına sahip: analitik sorgular sık, hesaplama motoru nadir değişir. Aynı dosyada olmaları her analitik eklemesinde hesaplama koduna dokunma riski yaratıyor.

Benzer şekilde `ImportService` 8 import akışını + eşleştirme altyapısını + batch kütüphanesini + rollback'i tek sınıfta tutuyor. Sekiz `Import*Async` metodu benzer iskeleti (hash al → header doğrula → kolon haritası kur → satır döngüsü → doğrula → logla) tekrarlıyor; bu iskelet bir taban sınıf veya pipeline'a çıkarılabilir.

**(b) Yetki kapsamının UI'ya bırakılması.** Yazma işlemlerinde servis katmanı rol doğruluyor (`EnsureAdminAsync`), ama **okuma kapsamı** UI'nın doğru parametre geçmesine bağlı. Örnek (`Reports.razor:662-666`):

```csharp
int? regionFilter = Session.IsBM ? Session.CurrentUser?.RegionId : null;
int? userFilter   = Session.IsST ? Session.CurrentUser?.UserId   : null;
bool po           = !Session.CanViewAll || publishedOnly;
var task1 = CalcSvc.GetResultsAsync(selectedYear, selectedQuarter, regionFilter, po, specificUserId: userFilter);
```

`GetResultsAsync` kendi başına "çağıran kim, ne görmeli" bilgisine sahip değil. Blazor Server'da istemci servisi doğrudan çağıramadığı için bu **bugün sömürülebilir bir açık değil**; ancak yeni bir sayfa eklenirken filtreyi geçirmeyi unutmak, tüm ST'lerin prim tutarlarını yanlış role gösterir. Kapsamı servis imzasına taşımak (ör. `GetResultsAsync(actorUserId, year, quarter, ...)` ve kapsamı içeride türetmek) bu sınıf hatayı yapısal olarak imkânsız kılar.

**(c) Şema yönetiminde üç paralel yol.** EF migration'ları (`Migrations/`, 11 adet), elle çalıştırılan scriptler (`db/`, 24 adet) ve `Program.cs` içindeki çalışma zamanı "self-heal" `ALTER TABLE` bloğu aynı anda mevcut. Self-heal pragmatik bir yaraya yamadır ve yorumu bunu itiraf ediyor ("Raporlar 'Bir hata oluştu'"), ama uzun vadede hangi ortamda hangi şemanın geçerli olduğunu belirsizleştirir. `MIGRATIONS.md` bu ikiliği dürüstçe dokümante etmiş — bu iyi; hedef, tek yola (migration) yakınsamak olmalı.

---

## 3.4 Satış Başarı Puanı — Tam Mekanik

Bu bölüm, sistemin ürettiği tek en önemli sayının nasıl oluştuğunu adım adım açıklar.

### 3.4.1 Katman 1 — Ürün seviyesinde realizasyon

Her prim'e tabi ürün için (`ScoreCalculator.ScoreProduct`):

```csharp
rate   = targetQty > 0 ? actualQty / targetQty : 0
capped = Clamp(rate, 0, 1.05)          // taban 0, tavan %105
score  = capped × weight / 100
```

Üç kural buraya gömülü:

| Kural | Değer | Gerekçe |
|---|---|---|
| **Üst sınır (cap)** | %105 | Hedefin çok üstünde satan bir ST, diğer ürünlerdeki eksiğini kapatamaz. Ağırlıklı toplamın "tek üründen şişmesini" engeller. |
| **Alt taban** | 0 | İade (net negatif satış) oranı 0'a kadar düşürür ama **eksiye geçirmez** — bir ürün diğerlerinin puanını yiyemez. |
| **Hedefsiz ürün** | rate = 0 | Hedefi olmayan ürün ağırlığı kadar puan **kaybettirir**. Bu önemli bir yan etki (aşağıya bakınız). |

`Rate` (ham) ve `CappedRate` (cap'li) **ayrı** saklanır — ekranda ST "%137 sattım ama %105 sayıldı" farkını `↑` işaretiyle görebilir. Şeffaflık açısından doğru bir tasarım.

### 3.4.2 Katman 2 — Satış skoru (realizasyon)

```
salesScore = Σ (her ürünün capped × weight / 100)
```

Ağırlıklar `ScorecardWeights` tablosundan, **o çeyrek için geçerli olan** versiyondan gelir (`ValidFrom <= quarterStart`). Scorecard ekranı ağırlık toplamını `Toplam: X/100` rozetiyle gösterir ve 100 değilse kırmızıya döner.

> **Bulgu (Orta):** Bu rozet yalnızca **görsel bir uyarı**dır — toplam 100 olmasa da kayıt engellenmiyor. Ağırlık toplamı 90 ise, hedefini birebir tutturan bir ST'nin satış skoru %100 değil %90 çıkar ve gate'e takılır. Kaydetmeyi bloke eden bir doğrulama (veya en azından onay diyaloğu) eklenmeli.

> **Bulgu (Orta):** `ScorecardWeight.MinRate` alanı (ürün başına "min oran", varsayılan %85) veritabanında tutuluyor, Scorecard ekranında düzenleniyor ve `ConfigSnapshot`'a yazılıyor — ancak **skor hesabında hiç kullanılmıyor.** `ScoreProduct(targetQty, actualQty, weight)` imzası `MinRate` almıyor. Aynı durum `KpiCriteria.MinRate` için de geçerli. Yani admin "min %85" girip kaydediyor, hiçbir etkisi olmuyor. Ya bu alan iş kuralına bağlanmalı (ör. min'in altındaki ürün 0 sayılsın) ya da ekrandan kaldırılmalı — bugünkü hâli yanıltıcı.

### 3.4.3 Katman 3 — Run-rate düzeltmesi (opsiyonel)

`SystemSettings.RunRateMode` açıksa:

```
soldRatio   = (sell-out verisi olan dönem sayısı) / (çeyrekteki dönem sayısı)
targetQty   = rawTarget × soldRatio
```

Örnek: Q2'nin yalnız Nisan–Mayıs verisi yüklendiyse `soldRatio = 2/3 ≈ 0,667`; 3 aylık hedef 2 aya orantılanır. Bu, "çeyrek bitmeden bakılan skorun sahte düşük çıkması" sorununu çözer. Varsayılan **kapalı**.

### 3.4.4 Katman 4 — Diğer üç KPI

| KPI | Formül | Veri kaynağı | Veri yoksa |
|---|---|---|---|
| **Ruta Uyum** | `RutaUyum.Rate` (dosyadan gelen hazır oran) | Ruta importu | `null` → 0 sayılır |
| **Dağıtım** | `min(satış yapılan brick / atanmış brick, 1.0)` | SellOuts + UserBricks | brick yoksa 0 |
| **Strike Rate** | `min(sipariş alınan planlı GLN / planlı GLN, 1.0)` | VisitPlans + Orders | `null` → **0 sayılır** |

### 3.4.5 Katman 5 — Final skor, gate ve prim

```
finalScore = 0.60·salesScore + 0.15·rutaScore + 0.15·distScore + 0.10·strikeScore
gate       = finalScore >= 0.85
primAmount = (isEligible && gate) ? RolePrimBase × TierMultiplier(finalScore) : 0
```

Ağırlıklar `KPICriteria` tablosundan okunuyor; tablo boşsa koddaki varsayılanlar (60/15/15/10) devreye giriyor.

> **Bulgu (Orta):** KPI ağırlıklarının toplamının 100 olması **hiçbir yerde doğrulanmıyor** — ne UI'da rozet var (ürün ağırlıklarında olduğu gibi) ne de kodda kontrol. Yanlışlıkla 60/20/15/10 girilirse (toplam 105) tüm skorlar şişer ve bu fark hiçbir yerde görünmez.

### 3.4.6 Sayısal örnek (uçtan uca)

Bir ST, üç ürünlü bir scorecard'da (ağırlıklar 50/30/20):

| Ürün | Hedef | Gerçekleşen | Ham oran | Cap'li | Ağırlık | Katkı |
|---|---|---|---|---|---|---|
| A | 1.000 | 1.150 | 1,150 | **1,050** ↑ | 50 | 0,525 |
| B | 800 | 700 | 0,875 | 0,875 | 30 | 0,2625 |
| C | 500 | 0 | 0,000 | 0,000 | 20 | 0,000 |
| | | | | | **100** | **satışScore = 0,7875** |

Diğer KPI'lar: Ruta %92 → 0,92 · Dağıtım 18/20 brick → 0,90 · Strike planı yok → **null → 0**

```
final = 0,60×0,7875 + 0,15×0,92 + 0,15×0,90 + 0,10×0      = 0,7455
gate  = 0,7455 >= 0,85 → HAYIR → prim = 0 ₺
```

Aynı ST'nin ziyaret planı yüklenmiş ve Strike %90 olsaydı:

```
final = 0,7455 + 0,10×0,90 = 0,8355 → yine gate altı (ama %9 daha yakın)
```

Örnek iki şeyi görünür kılıyor: **(1)** C ürününün hiç satılmaması tek başına 0,20 × 0,60 = **0,12 final puan** kaybettiriyor; **(2)** eksik ziyaret planı verisi **0,10 final puan** kaybettiriyor ve bu kayıp ST'nin performansından değil, veri yükleme eksikliğinden kaynaklanıyor.

### 3.4.7 Kodla dokümantasyon çelişkisi

`KpiBreakdownBars.razor` bileşeninin parametre açıklaması şöyle diyor:

```csharp
/// <summary>Sipariş (strike rate) skoru — null ise FinalScore'da yeniden ağırlıklandırılır</summary>
[Parameter] public decimal? OrderScore { get; set; }
```

Ancak `ScoreCalculator.FinalScore` **yeniden ağırlıklandırma yapmıyor**; `(orderScore ?? 0m) * orderWeight` ile sıfır sayıyor. Bu, kodun kendi dokümantasyonuyla çeliştiği tek yer ve §6.3'teki bulguyu güçlendiriyor: ya niyet yeniden ağırlıklandırmaktı ve uygulanmadı, ya da yorum güncellenmedi. Her iki durumda da iş sahibiyle netleştirilmesi gereken bir belirsizlik.

---

## 3.5 Veri Okuma Hattı — Sistem Excel'i Nasıl Okuyor?

Sekiz import tipinin tamamı aynı sekiz aşamalı hattı izliyor. Aşağıda Sell-Out örneği üzerinden anlatılmıştır (`ImportService.ImportSellOutAsync`).

### Aşama 1 — Yetki ve dosya alma

```
Admin doğrulaması (DB'den) → stream tamamen belleğe alınır → SHA256 hash üretilir
→ aynı hash daha önce başarıyla yüklenmiş mi? (ImportLogs sorgusu)
```

Dosya `InputFile` bileşeniyle alınır (`accept=".xlsx,.xls"`, açık boyut limiti ile). Hash, kullanıcıya "bu dosyayı 16.06.2026'da zaten yüklediniz" uyarısı olarak döner — engellemez, bilgilendirir.

### Aşama 2 — Şema hazırlık kontrolü

111 bin satır ayrıştırılmadan **önce** hedef tablonun varlığı kontrol edilir:

```sql
SELECT CAST(CASE WHEN OBJECT_ID('dbo.SellOutDetails','U') IS NOT NULL THEN 1 ELSE 0 END AS int)
```

Tablo yoksa kriptik SQL hatası yerine "db/sellout-gln.sql çalıştırılmamış" mesajı döner. Bu, operasyonel açıdan düşünülmüş bir detay.

### Aşama 3 — Kolon haritası (konum değil, **ad** bazlı)

```csharp
var colMap = BuildColumnMap(ws, headerRow: 2, new[] {
    ("Ay",    h => h == "AY"),
    ("Brick", h => h.Contains("BRICK")),
    ("GLN",   h => h.Contains("GLN")),
    ("Marka", h => h.Contains("MARKA")),
    ("Kutu",  h => h.Contains("KUTU")),
    ...
});
```

Başlık satırı normalize edilerek taranır. Sonuç: kaynak sistem araya bir "ST" kolonu eklerse veya kolon sırasını değiştirirse import bozulmaz. Zorunlu alanlar (`Ay`, `Brick`, `Marka`, `Kutu`) bulunamazsa **hangi kolonun eksik olduğu isim isim** raporlanır.

### Aşama 4 — Eşleştirme haritalarının kurulması

Satır döngüsüne girmeden önce dört sözlük belleğe alınır — böylece 111 bin satır için 111 bin DB sorgusu yerine 4 sorgu yapılır:

| Harita | İçerik | Ek kaynak |
|---|---|---|
| `productMap` | normalize(ad) → ProductId | + `ImportAlias` (çoklu, `;` `\|` ayraçlı) |
| `brickMap` | normalize(brick adı) → BrickId | + `NameAliases` (hatırlanan eşlemeler) |
| `userBrickMap` | BrickId → UserId | aktif atamalar |
| `periodMap` | "2026-04" → PeriodId | |

Anahtarların tamamı `Normalize()` üzerinden geçer: trim → çoklu boşluk tekleme → **Türkçe nokta sorunu** (i/İ/ı/I → hepsi `I`) → `ToUpperInvariant`. ş/ç/ğ/ü/ö bilinçli olarak katlanmaz.

### Aşama 5 — Toplu hücre okuma (performans)

```csharp
var data = (object[,])ws.Cells[dim.Address].Value;   // TEK çağrı — tüm sayfa belleğe
```

Yorumdaki nota göre bu, hücre hücre EPPlus erişimine kıyasla **10–30× hızlanma** sağlıyor. 100 bin+ satırlık InfiniLens dosyaları için kritik.

### Aşama 6 — Satır döngüsü ve eleme sırası

Her satır şu kapılardan sırayla geçer:

```
1. Brick boş / "-" / "(BRICK ATANMAMIŞ)"     → atla (skipped++)
2. Marka boş                                  → atla
3. Ay "YYYY-MM" formatında parse edilemiyor   → atla
4. Dönem haritada yok                         → unmatchedPeriods'a ekle, atla
5. Brick haritada yok                         → unmatchedBricks'e ekle, atla
6. Marka haritada yok                         → unmatchedProducts'a ekle, atla
7. Sayısal değerler ParseCellNumber ile okunur
```

Elenen her değer **distinct olarak biriktirilir** — kullanıcıya "142 satır atlandı" değil, "şu 7 farklı marka eşleşmedi: ..." denir. Bu fark, sorunu çözülebilir kılan şeydir.

**Sayı okuma** (`ParseCellNumber`) üç kademeli: ham `.Value` sayısalsa doğrudan → değilse tr-TR ve InvariantCulture denenir → son çare ayraçlar normalize edilir. Boş hücre `null` ("yok"), dolu ama çözülemeyen hücre de `null` döner ve çağıran bunu **hata olarak raporlar** (sessiz 0 yazmaz).

**Negatif değerler atlanmaz** — iade/düzeltme olarak net sell-out'a dahil edilir.

### Aşama 7 — Toplama ve iki tabloya yazma

Aynı `brick + marka + dönem` birden çok GLN satırına bölünmüş olabilir:

- `SellOuts` → bu üçlünün **toplamı** (skor için)
- `SellOutDetails` → her GLN satırı **ayrı ayrı** (analiz için)

Dosya içinde tekrar eden anahtar toplanır (`touchedThisBatch` kümesiyle takip); önceki import'tan kalan kayıt ise dosyadaki ilk değerle **değiştirilir**. Bu ayrım olmasa aynı dosya iki kez yüklendiğinde miktarlar katlanırdı.

### Aşama 8 — Doğrulama ve kayıt

```
fileTotal (bellekte toplanan kutu) ↔ dbTotal (yazıldıktan sonra DB'den okunan)
AmountsVerified(db, file, rowCount) → tolerans = max(1, satır × 0,0001)
```

Tolerans, `decimal(18,4)` yuvarlamasından kaynaklanan ~0,5 birimlik yanlış-negatifleri elerken gerçek tutmazlığı (yüzlerce birim) yakalar. Sonuç `ImportResult.Verified` bayrağı olarak ekrana yansır.

Tüm yazma **tek transaction** içinde, tüm satırlar aynı `batchTime` damgasıyla — bu damga daha sonra rollback'in tam eşleşme anahtarı olur.

### İki aşamalı onay (dry-run kapısı)

Kullanıcı dosyayı seçtiğinde **önce `dryRun: true`** çalışır; hiçbir şey yazılmadan bir önizleme kartı üretilir:

- Kaç marka/brick/dönem eşleşti, hangileri eşleşmedi
- Bulanık öneriler ("BENGAY JEL → BENGAY, ✓ Eşle" butonu — kabul edilirse kalıcı alias olur ve önizleme **anında yeniden çalışır**)
- Boş GLN satırı sayısı
- Brick→ST haritasında kaç atama değişecek
- **⛔ Kırmızı kapı:** eşleşmeyen marka varsa "bu satırlar atlanır, satış skora YANSIMAZ" uyarısı + çözüm yönergesi

Ancak **"Onayla ve Yükle"** butonuna basılınca `dryRun: false` ile gerçek yazma yapılır. Bu iki aşamalı akış, prim verisine yanlış dosya girmesini önleyen en etkili mekanizma.

---

## 4. İşlevler — Modül Modül

### 4.1 Veri İçe Aktarma (`/import`)

**8 dosya tipi:** Sell-Out (InfiniLens DistInvTran, 11 kolonlu GLN formatı), Sipariş, Hedef, Ruta Uyum, Ziyaret Planı, Eczane Planı (UTT_Brick), Ürün-Marka master (Barkod-Product), Brick atama.

Öne çıkan yetenekler:

- **Dry-run önizleme** — hiçbir şey yazılmadan eşleşme kapsamı raporlanır: kaç marka/brick/dönem eşleşti, eşleşmeyenler hangileri, hangi öneriler var, dosya toplamı ile beklenen DB toplamı tutuyor mu.
- **Çift yükleme tespiti** — SHA256 hash `ImportLogs`'a yazılır; aynı içerik daha önce yüklendiyse tarihi gösterilir.
- **Bulanık eşleştirme önerisi** — Levenshtein benzerliği ≥ 0.82 eşiğiyle en yakın aday önerilir, **otomatik eşleme yapılmaz**; admin onaylarsa `NameAlias` (brick/ST) veya `Product.ImportAlias` (ürün) olarak kalıcılaşır ve sonraki importlarda otomatik çözülür. Bu, "insan onayı olmadan veri sessizce yanlış yere yazılmaz" ilkesinin somut uygulaması.
- **Ad bazlı kolon haritası** (`BuildColumnMap`) — sabit kolon indeksi yerine başlık adına göre eşleşme; araya kolon eklenmesi importu bozmaz.
- **Kültür-bağımsız sayı okuma** (`ParseCellNumber`) — EPPlus'ın ham `.Value`'su sayısalsa doğrudan kullanılır. Yorumda önceki hatanın kaydı var: `.Text` + naif `Replace(",",".")` yöntemi "1.538" → 1,538 çeviriyordu, yani **≥1000 tüm değerler 1000 kat küçülüyordu**. Bu, prim hesabını temelden bozacak bir hataydı ve düzeltilmesi yorumla belgelenmiş.
- **Yükleme Kütüphanesi** (`ImportBatch`) — ham dosya içeriği DB'de saklanır; geçmiş bir dosya tek tıkla yeniden aktifleştirilebilir.
- **Atomik rollback** — `ImportedAt` tam eşleşmesiyle (eski ±60 sn pencereli yöntemin çapraz-import silme riski giderilmiş), tek transaction içinde detay + toplam + log silinir; eşzamanlı çift tık `DbUpdateConcurrencyException` yakalanarak zarifçe ele alınır.
- **Brick→ST harita senkronu** (`SyncBrickAssignmentsAsync`) — "bir brick'in tek aktif ST'si olur" invariantını uygular.

### 4.2 Hesaplama (`/calculate`)

`CalculateQuarterAsync` akışı:

1. Çeyrek doğrulaması + admin yetki kontrolü
2. Dönem ID'leri, run-rate oranı (`soldPeriods / totalPeriods`)
3. **Geçmişe dönük ağırlık seçimi** — `ValidFrom <= quarterStart` olan en son ağırlık; eşit `ValidFrom`'da deterministik tie-break (`max WeightId`). Hiç tarihsel kayıt yoksa aktif scorecard'a düşer.
4. Dört parametreli ham SQL: hedefler, sell-out, brick-satış sayımı, siparişler — hepsi `UserBricks` JOIN'i ve `SharePercent` çarpanıyla
5. Ruta / Strike verileri (duplicate-key çökmesine karşı `GroupBy().First()` savunması)
6. KPI ağırlıkları — `PickKpi` ile deterministik seçim (çift aktif kayıt riskine karşı)
7. ST bazlı skorlama + `ConfigSnapshot` serileştirme
8. **Tek transaction içinde** eski hesaplamaları sil + yenilerini ekle

Notlar:
- Hesaplama tamamen bellekte yapılıp transaction en sona bırakılmış — kilit süresini minimumda tutan doğru yaklaşım.
- Dış `try/catch` tüm hatayı yakalayıp `result.Error`'a yazıyor; iç exception mesajı iki seviye derinden çıkarılıyor (`ex.InnerException?.InnerException?.Message`) — SQL hatalarını kullanıcıya anlaşılır iletmek için pratik ama exception mesajının UI'ye sızması küçük bir bilgi ifşası riski taşır.

### 4.3 Prim Yapılandırması (`/prim-config`)

- `PrimTiers`: skor aralığı → çarpan (ör. 0.85–0.95 → 1.0)
- `RolePrimBases`: rol → TL taban tutar
- `SystemSettings`: `SalesOnlyMode` (görüntüleme), `RunRateMode` (hesaplama)
- `StarCriteria`: yıldız badge metriği (kutu / kurum sayısı / TL / MF TL)

### 4.4 Yayın Akışı

Üç durumlu model: **hesaplandı** (taslak, yalnız admin görür) → **yayınlandı** (ST/BM görür, ST'lere bildirim gider) → **bekletildi** (`IsPaused`, geçici gizleme). `CalcFreshnessBanner` brick ataması veya ağırlık son hesaplamadan sonra değiştiyse "yeniden hesapla + yayınla gerekli" uyarısı gösterir.

### 4.5 Analitik ve Raporlama

| Sayfa | İçerik |
|---|---|
| `/` Ana Sayfa (971 satır) | Yıldız badge'leri, admin analitik kartları, leaderboard modalı, freshness bandı |
| `/reports` (918) | ST bazlı skor tablosu, ürün kırılımı, bölge özeti, brick/eczane performansı, Excel dışa aktarım |
| `/eczaneler` (330) | Brick özeti → eczane (GLN) drill-down, planlı/plansız eczane filtresi |
| `/overview` (615) | Yonetici için Türkiye → bölge → ST → ürün drill-down |
| `/bolgem` (591) | BM için bölge infografiği |
| `/activity` (333) | Satış etkinliği trendi, ST aktivite metrikleri, bölge-ürün matrisi |
| `/panel` (327) | Admin kontrol paneli |

Excel çıktıları EPPlus ile **gerçek sayı ve formül** olarak üretiliyor (metin değil): otomatik filtre, pencere dondurma, yazdırma ayarları, toplam satırında `SUM` formülü. Bu, çıktının Excel'de kullanılabilir olması açısından önemli bir detay.

### 4.6 Yan Modüller

- **Görevler** (`/gorevler`) — BM kendi bölgesindeki ST'lere görev atar (tek tek veya tüm ekibe), ST durum günceller + yorum yazar; her adımda bildirim.
- **Saha Fotoğrafı** (`/photos`, `/photo-review`) — ST yükler (maks. 5 MB, `varbinary` olarak DB'de), Admin veya ilgili bölge BM'si onaylar/reddeder.
- **Bildirim Merkezi** — hedefli gönderim: kullanıcı, rol, bölge, yönetici ekibi.
- **Organizasyon** (`/organization`, 805 satır) — kullanıcı CRUD, aktif oturum görüntüleme, oturum geçmişi, parola sıfırlama (tek/toplu), **impersonation**.

---

## 4.7 Satış Temsilcisinin Kendi Realizasyonunu Görmesi

Sistemin en çok kullanılacak ekranı buradır: her ST kendi hesabıyla girip kendi performansını görür. Bu, klasik "prim listesi Excel'i e-postayla dağıtılır" modelinin yerini alan **self-servis** katmanıdır.

### 4.7.1 ST'nin gördüğü veri nasıl daraltılıyor

İki kademeli filtre uygulanıyor (`Home.razor:745-747`):

```csharp
int? userFilter = Session.IsST ? Session.CurrentUser?.UserId : null;
publishedSummaries = await CalcSvc.GetResultsAsync(
    publishedYear, publishedQuarter, regionFilter,
    publishedOnly: !isDraftView,      // ST için daima true
    specificUserId: userFilter);       // ST için daima kendi ID'si
```

Sonuç: ST'nin listesi **tek satırdır** — kendisi. Ayrıca:

- **Yayınlanmamış hesaplama ST'ye hiç gitmez.** `GetLatestPublishedAsync()` yalnız `IsPublished && !IsPaused` olan dönemi döndürür; admin taslak görünüme düşebilir (`GetLatestCalculatedAsync`), ST düşemez. Yorum bunu açıkça belirtiyor: *"ST/BM davranışı değişmez — yayın yoksa pano görmemeleri doğru."*
- **Bölge tablosu ST'ye gizlenir** (`Reports.razor:236`: `@if (regionRows.Any() && !Session.IsST)`) — başka bölgelerin geçiş oranlarını göremez.
- **Sipariş sıralaması (leaderboard)** ST'ye ancak admin `PublishOrderLeaderboardAsync` ile paylaştıysa açılır (`orderShared` bayrağı). Yani rekabet görünürlüğü admin kontrolünde.

### 4.7.2 Ana sayfada gördüğü kişisel kart

ST giriş yaptığında `/` adresinde tek büyük kart açılır. Kartın rengi ve kenarlığı sonucuna göre değişir (yeşil geçti / kırmızı kaldı / amber prim dışı). İçeriği:

| Bileşen | Gösterdiği |
|---|---|
| **`ScoreGauge`** (150 px SVG halka) | Final skor yüzdesi; halkada **%85 gate çentiği** çizili, halka 0–105 ölçeğinde. Renk: ≥gate yeşil, ≥%70 amber, altı kırmızı |
| **`KpiBreakdownBars`** | 4 KPI'nın ayrı barları — her birinin yanında **"· ağırlık %60"** etiketi. Veri yoksa bar %45 opaklıkla ve "—" ile soluk gösterilir |
| **Prim kartı** | `PrimAmount` varsa `28 px` yeşil monospace ile `12.500,00 ₺`; yoksa gerekçesi yazılı: *"Prim dışı dönem"* / *"Eşik aşılamadı"* / *"Tanımlı kademe yok"* |
| **Ürün detayı listesi** | Her ürün için `Hdf: 1.000 · Grc: 1.150 · %115,0 ↑` — `↑` cap uygulandığını gösterir |
| **KPI mini kartları** | Ruta / Dağıtım / Strike Rate ayrı kutularda, %85 eşiğine göre renkli |
| **Radar grafiği** (Chart.js) | 4 KPI'yı 0–100 ölçeğinde çizer, üzerine **kesikli %85 eşik çemberi** bindirir — "hangi eksende eksiğim var" sorusunu tek bakışta yanıtlar |
| **Sipariş özeti** | `orderShared` açıksa kendi sipariş kutu/TL/kurum sayısı |

Bu kart, "neden bu prim" sorusunun cevabını itiraz gerektirmeden veriyor: skor → 4 bileşen → ürün bazlı hedef/gerçekleşen → cap işareti. Bir prim sisteminde beklenen şeffaflık seviyesinin üstünde.

### 4.7.3 Raporlar sayfasında gördükleri

`/reports` sayfasında ST kendi satırını sıralama tablosunda görür (tek satır) ve altında **kendi kapsamına daraltılmış** iki panel açılır (`Reports.razor:524-537`):

```csharp
int? bpUserId = Session.IsST ? Session.CurrentUser?.UserId : null;
var bpBrickTitle = Session.IsST ? "Brick Performansım" : ...
var bpPharmTitle = Session.IsST ? "Bricklerimdeki En İyi Eczaneler" : ...
```

- **"Brick Performansım"** — atanmış brick'lerinin her birinde hedef/gerçekleşen ve realizasyon barı
- **"Bricklerimdeki En İyi Eczaneler"** — GLN bazında en çok satış yapılan eczaneler

`/eczaneler` sayfasında da aynı daraltma var: ST yalnız kendi brick'lerindeki eczaneleri görür, planlı/plansız filtresiyle "hangi eczaneyi ziyaret planımda olduğu hâlde satış alamadım" sorusunu çalışabilir. Bu, prim ekranından **aksiyona dönüşen** bir analitik katman.

### 4.7.4 Aksiyon alabildiği alanlar

ST pasif bir izleyici değil:

- `/gorevler` — BM'nin atadığı görevlerin durumunu günceller, yorum yazar
- `/photos` — saha fotoğrafı yükler (maks. 5 MB), onay durumunu takip eder
- `/notifications` — yayın bildirimleri ve yöneticiden gelen mesajlar
- `/change-password` — ilk girişte `MustChangePassword` ile zorunlu yönlendirme

### 4.7.5 ST deneyimindeki boşluklar

| Bulgu | Etki |
|---|---|
| **Geçmiş dönem karşılaştırması yok** | ST yalnız *son yayınlanan* çeyreği görüyor. "Geçen çeyrekte %78 idim, şimdi %84" bilgisi yok — gelişim algısı oluşmuyor |
| **Simülasyon yok** | "C ürününden 200 kutu daha satarsam skorum ne olur" sorusu yanıtsız. Veri (hedef, gerçekleşen, ağırlık) zaten ekranda — hesap istemci tarafında yapılabilir, düşük maliyetli yüksek etkili bir ekleme |
| **Mobil uyum belirsiz** | Kartlar sabit `grid-template-columns:repeat(4,1fr)` ve `min-width` değerleriyle kurulu; saha ekibi büyük ihtimalle telefondan bakacak. Responsive davranış test edilmemiş görünüyor |
| **İtiraz kanalı yok** | ST skoruna itiraz etmek isterse sistem içinde bir yol yok; süreç sistem dışına taşıyor |
| **Boş durum zayıf** | Henüz yayın yoksa ST'ye anlamlı bir "sonuçlar hazırlanıyor" mesajı yerine boş pano düşüyor |

---

## 4.8 Web Tabanlı Çalışma ve Canlıya Alma Mimarisi

Proje bir masaüstü uygulaması değil — **sunucuda çalışan, tarayıcıdan erişilen çok kullanıcılı bir web uygulaması**. Bu, mimarinin en belirleyici özelliği ve beraberinde belirli gereksinimler getiriyor.

### 4.8.1 Çalışma modeli: Blazor Server / SignalR

```
Tarayıcı ──WebSocket (SignalR circuit)──► Sunucu (Kestrel/IIS)
   │                                          │
   │  DOM farkları aşağı                      │  C# kodu SUNUCUDA çalışır
   │  olay (tık, input) yukarı                │  Razor bileşen state'i sunucuda
   │                                          ▼
   └──────────────────────────────────► SQL Server
```

`App.razor`'daki yapılandırma:

```razor
<Routes @rendermode="@(new InteractiveServerRenderMode(prerender: false))" />
```

Bunun pratik sonuçları:

| Özellik | Sonuç |
|---|---|
| **Kod istemciye inmez** | Prim formülü, ağırlıklar, SQL — hiçbiri tarayıcıya gitmez. WebAssembly'de olmayan doğal bir güvenlik avantajı |
| **Her kullanıcı = bir sunucu circuit'i** | Bileşen state'i sunucu belleğinde tutulur; 50 eşzamanlı kullanıcı = 50 canlı circuit |
| **Kalıcı WebSocket zorunlu** | Reverse proxy / yük dengeleyici WebSocket'e izin vermeli, aksi halde uygulama sürekli "bağlantı kesildi" gösterir |
| **Ağ gecikmesi UI'ye yansır** | Her tık sunucuya gidip döner. LAN/VPN içinde sorunsuz; yüksek gecikmeli bağlantıda hissedilir |
| `prerender: false` | İlk ekran boş gelir, circuit kurulunca dolar. `MainLayout` bunu `GlobalLoading` ile gizliyor — oturum restore edilmeden hiçbir sayfa render edilmiyor (yenilemede "giriş yapın" flash'ını önlemek için bilinçli bir tercih) |

### 4.8.2 Oturumun sunucu tarafında tutulması

```
Login → Guid token üretilir
      → SessionStore (Singleton, ConcurrentDictionary) belleğe user yazar
      → ProtectedSessionStorage'a şifreli token + userId tarayıcıya yazılır
      → UserSessions tablosuna DB kaydı (IP, LoginAt, LastSeenAt)
      → 5 dakikada bir heartbeat LastSeenAt'i günceller
```

Sunucu yeniden başlarsa: bellek boşalır → `RestoreSessionAsync` tarayıcıdaki `userId`'yi okuyup DB'den kullanıcıyı geri yükler. Bu yüzden **DataProtection anahtarları diske kalıcı yazılıyor** — aksi halde her restart tüm oturumları düşürürdü.

`SessionStore` 8 saat hareketsizlikte token'ı temizler; temizlik 15 dakikada bir timer ile çalışır.

### 4.8.3 Canlıya alma gereksinimleri

Kod tabanından çıkarılabilen zorunluluklar:

| Gereksinim | Neden |
|---|---|
| **Windows Server + IIS** (veya Linux + Kestrel/nginx) | .NET 8 self-contained çalışabilir; `launchSettings.json`'da IIS Express profili var, üretim için IIS + ASP.NET Core Hosting Bundle en olası yol |
| **WebSocket açık reverse proxy** | SignalR circuit'i için zorunlu |
| **Sticky session (session affinity)** | `SessionStore` ve `LoginThrottle` bellek içi — kullanıcı her istekte aynı sunucuya düşmeli. Çok sunuculu kurulumda **şart** |
| **HTTPS sertifikası** | `Program.cs` prod'da `UseHsts()` + `UseHttpsRedirection()` uyguluyor |
| **Environment variable'lar** | `ConnectionStrings__DefaultConnection`, `Smtp__Password`, `AppBaseUrl`, `AllowedHosts` — `SECURITY-SETUP.md`'de dokümante |
| **SQL Server erişimi** | Uygulama hesabı bugün **DDL yetkisi de istiyor** (başlangıçtaki `ALTER TABLE` self-heal bloğu) |
| **Yazılabilir disk** | Serilog `logs/prim-YYYYMMDD.log` (30 gün) + DataProtection anahtar klasörü (`LocalApplicationData`) |
| **İnternet erişimi (istemci)** | Chart.js **CDN'den** yükleniyor (`cdn.jsdelivr.net`) — kapalı ağda grafikler çalışmaz |

### 4.8.4 Canlıya alma bulguları

| # | Bulgu | Öneri |
|---|---|---|
| **W1** | **Chart.js dış CDN'den** yükleniyor. Kurumsal ağ dışarı kapalıysa tüm grafikler sessizce boş kalır (`if (typeof Chart === 'undefined') return;` — hata bile vermez). SRI hash'i doğru şekilde konmuş, ama erişilebilirlik varsayımı riskli | Kütüphaneyi `wwwroot`'a indirip yerelden sun |
| **W2** | **Health check endpoint'i yok** — yük dengeleyici veya izleme aracı uygulamanın ayakta olup olmadığını anlayamaz | `app.MapHealthChecks("/health")` + DB kontrolü ekle |
| **W3** | **SignalR circuit limitleri yapılandırılmamış** — `MaxBufferedUnacknowledgedRenderBatches`, `DisconnectedCircuitRetentionPeriod`, `JSInteropDefaultCallTimeout` varsayılanda. Yoğun kullanımda bellek baskısı oluşabilir | `AddInteractiveServerComponents(o => ...)` ile açıkça ayarla |
| **W4** | **Bağlantı kopma deneyimi ele alınmamış** — Blazor'ın varsayılan "Bağlantı kesildi, yeniden bağlanılıyor" overlay'i özelleştirilmemiş, Türkçeleştirilmemiş | `<div id="components-reconnect-modal">` ile özel mesaj tanımla |
| **W5** | **Yatay ölçekleme mümkün değil** (sticky olmadan). Kullanıcı sayısı artarsa tek sunucuyu büyütmek (scale-up) dışında yol yok | Redis backplane + dağıtık `SessionStore` gerekirse |
| **W6** | **Uygulama hesabı DDL yetkisi istiyor** — güvenlik açısından minimum yetki ilkesine aykırı | Self-heal bloğunu kaldır, şemayı dağıtım adımında uygula |
| **W7** | **Dağıtım runbook'u yok** — hangi sırayla migration, hangi env var, geri alma nasıl | Yazılı prosedür oluştur |
| **W8** | **Fotoğraflar ve import dosyaları DB'de** (`varbinary`) — yedek boyutu ve geri yükleme süresi hızla büyür | Blob/dosya sistemine taşı |

### 4.8.5 Ölçek tahmini

Kod tabanındaki ipuçlarına göre (111 bin satırlık sell-out dosyaları, ~27 bin eczane, ~500 brick, ST sayısı onlarca) sistem **tek sunucuda rahatça çalışacak** bir ölçekte. Blazor Server'ın circuit maliyeti (kullanıcı başına birkaç yüz KB–birkaç MB) bu kullanıcı sayısında sorun değil. Asıl darboğaz kullanıcı sayısı değil, **import ve analitik sorgu süreleri** olacaktır — bu yüzden `CommandTimeout` değerlerinin 30–120 sn arasında açıkça set edilmiş olması yerinde.

---

## 5. Güvenlik, Yetkilendirme ve Denetim

### 5.0 Kimlik Doğrulama Akışı

Sistem ASP.NET Core Identity'yi **kullanmıyor** — kendi hafif kimlik katmanını kuruyor. (Paket referansları arasında `Identity.EntityFrameworkCore` ve `JwtBearer` var ama `Program.cs`'te `AddAuthentication`/`AddAuthorization` çağrısı yok; bunlar kullanılmayan bağımlılıklar.)

```
1. Kullanıcı adı VEYA e-posta + parola
2. LoginThrottle.GetLockRemaining(username) → kilitliyse LoginLockedException
3. DB: Users WHERE (Username=@u OR Email=@u) AND IsActive AND NOT IsDeleted
4. BCrypt.Verify(password, PasswordHash)
   ├─ başarısız → throttle.RegisterFailure() → null döner
   └─ başarılı  → throttle.Reset()
5. Önceki aktif UserSession kapatılır (IsActive=false, LogoutAt)
6. Yeni UserSession oluşturulur (IP, LoginAt, LastSeenAt)
7. User.ActiveSessionId güncellenir
8. SessionState.LoginAsync → Guid token + SessionStore + ProtectedSessionStorage
9. MustChangePassword ise /change-password'a zorunlu yönlendirme
```

**Gözlem — "tek oturum" yarım uygulanmış:** Adım 5 önceki oturumu DB'de kapatıyor ve `ActiveSessionId` güncelleniyor, ancak **eski tarayıcının `SessionStore` token'ı geçersizleşmiyor**. Yani aynı hesapla ikinci bir cihazdan girildiğinde ilk cihaz çalışmaya devam eder; DB kaydı "kapalı" görünse de oturum fiilen yaşar. Niyet tek-oturum kısıtıysa eksik; niyet yalnız takipse dokümante edilmeli.

**Gözlem — giriş/çıkış audit'e yazılmıyor.** `UserSessions` tablosu kimin ne zaman girdiğini tutuyor (bu iyi), ama `AuditLogs`'ta `Login`/`Logout`/`LoginFailed` aksiyonu yok. Başarısız giriş denemeleri hiçbir kalıcı yere yazılmıyor — yalnız bellekteki sayaçta. Güvenlik olayı incelemesi gerektiğinde bu boşluk hissedilir.

### 5.1 Yetkilendirme Mimarisi — İki Katman

**Katman 1 — UI guard'ları** (`SessionState` üzerinden, rol adına göre):

```csharp
public bool IsAdmin  => RoleName == "Admin";
public bool CanManage    => IsAdmin;
public bool CanViewAll   => IsAdmin || IsYonetici;
public bool CanSendNotification => IsAdmin || IsYonetici || IsBM;
```

Her sayfa başında iki aşamalı kontrol:

```razor
@if (!Session.IsLoggedIn)  { <LoginRequired /> }
else if (!Session.CanManage) { <NotAuthorizedView /> }
```

**Katman 2 — Servis katmanı doğrulaması** (DB'den, rol **ID**'sine göre):

```csharp
private async Task EnsureAdminAsync(int userId) {
    var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId);
    if (actor == null || actor.RoleId != RoleId.Admin)
        throw new UnauthorizedAccessException("Bu işlem için yetkiniz yok.");
}
```

Bu ikinci katman kritik: UI'daki `RoleName` string karşılaştırması bellekteki (potansiyel olarak bayat) `User` nesnesinden gelirken, servis katmanı **her çağrıda DB'ye taze sorar**. Bir kullanıcının rolü düşürülürse ikinci katman anında etkili olur.

**Yetki matrisi — servis katmanında zorlanan kontroller:**

| Servis | Kontrol | Metot sayısı |
|---|---|---|
| `ImportService` | `EnsureAdminAsync` | 16 |
| `CalculationService` | `EnsureAdminAsync` | 14 |
| `AuthService` | `EnsureAdminAsync` | 2 (toplu/tekil parola sıfırlama) |
| `ExportService` | `EnsureCanExportAllAsync` (Admin **veya** Yonetici) | 1 (ham hesaplama dökümü) |
| `NotificationService` | `EnsureCanSendBulkAsync` | 1 (toplu bildirim) |
| `PhotoService` | `CanReviewAsync` (Admin veya **ilgili bölgenin** BM'si) | onay/ret + görüntü okuma |
| `TaskService` | BM–ST ilişki doğrulaması (kendi bölgesi dışına görev atayamaz) | atama + durum güncelleme |
| `SessionState` | `IsAdmin` + **admin'e bürünme yasağı** | impersonation |

**Boşluk:** Yukarıdaki tablo tamamen **yazma** işlemlerini kapsıyor. **Okuma** kapsamı (§3.3-b) servis imzasına taşınmamış — `GetResultsAsync`, `GetPharmaciesForScopeAsync`, `GetBrickPerformanceAsync` gibi metotlar "çağıran kim" bilgisini almıyor, filtreyi UI'nın geçirdiği parametreden alıyor. Blazor Server'da istemci bu servisleri doğrudan çağıramadığı için bugün sömürülebilir değil; ancak yeni sayfa eklenirken filtre unutulursa yanlış rolde veri görünür ve bunu yakalayacak bir test yok.

### 5.2 Denetim (Audit) Envanteri

`AuditLogs` tablosuna yazılan **28 farklı aksiyon** tespit edildi:

| Kategori | Aksiyonlar |
|---|---|
| **Kimlik / parola** | `PasswordChange`, `PasswordAdminReset`, `PasswordBulkReset`, `PasswordTokenReset` |
| **Kimliğe bürünme** | `ImpersonateStart`, `ImpersonateStop` |
| **Veri içe aktarma** | `Import`, `ImportRollback`, `ReactivateBatch`, `DeleteBatch`, `MatchAlias`, `InstitutionBrick` |
| **Yayın** | `Publish`, `Unpublish`, `PauseCalculation`, `ResumeCalculation` |
| **Sıralama** | `PublishOrderLeaderboard`, `StopOrderLeaderboard`, `PauseOrderLeaderboard`, `ResumeOrderLeaderboard` |
| **Yapılandırma** | `SavePrimTiers`, `SaveRolePrimBase`, `SetSalesOnlyMode`, `SetRunRateMode`, Scorecard ağırlık değişiklikleri (3 çağrı) |
| **Operasyonel** | `AssignTask`, `AssignTeamTask`, `UpdateTaskStatus`, `UploadPhoto` |

Kayıt yapısı: `UserId · Action · EntityType · EntityId · OldValue · NewValue · IpAddress · CreatedAt`. `AuditService` hatayı yutup logluyor — audit yazımının ana işlemi düşürmemesi doğru karar.

**Denetim boşlukları — kritik olandan başlayarak:**

| # | Eksik | Etki |
|---|---|---|
| **D1** | **`Calculate` aksiyonu yok.** Hesaplama çalıştırma audit'e yazılmıyor | Prim tutarını değiştiren en önemli işlem izsiz. "Kim, ne zaman, hangi sonucu ezdi" cevaplanamaz — §6.1'deki veri kaybıyla birleşince ciddi |
| **D2** | **`Login` / `Logout` / `LoginFailed` yok** | Güvenlik olayı incelemesi yapılamaz; başarısız denemeler hiç kalıcı kaydedilmiyor |
| **D3** | **Kullanıcı CRUD audit'i belirsiz** | Kullanıcı ekleme/rol değiştirme/pasifleştirme için audit çağrısı görülmedi. Rol yükseltme izlenemez |
| **D4** | **`OldValue`/`NewValue` tutarsız dolduruluyor** | Bazı çağrılarda dolu, bazılarında `null`. "Ne değişti" bilgisi her kayıtta yok |
| **D5** | **`IpAddress` alanı çoğu kayıtta boş** | `AuditService.LogAsync` imzasında IP parametresi yok; alan var ama beslenmiyor. Yalnız `UserSessions` IP tutuyor |
| **D6** | **Audit görüntüleme ekranı yok** | Tablo doluyor ama arayüzde denetim kaydını okuyacak bir sayfa bulunamadı — veriye ancak SQL ile ulaşılıyor |
| **D7** | **Saklama/arşivleme politikası yok** | `AuditLogs` sınırsız büyür |

### 5.3 Güçlü yönler

| Alan | Uygulama |
|---|---|
| Parola saklama | BCrypt (`BCrypt.Net-Next 4.0.3`), ilk girişte zorunlu değiştirme |
| Parola politikası | Sunucu tarafı: ≥8 karakter + en az bir harf + bir rakam |
| Üretilen parolalar | `RandomNumberGenerator` (kriptografik), karıştırılabilir karakterler (0/O, 1/I/l) çıkarılmış, politika uyumu garantili |
| Reset token | 32 bayt kriptografik rastgele; **DB'de yalnızca SHA256 hash'i**, 1 saat geçerlilik, kullanımda temizlenir |
| Brute-force | 15 dk'da 5 hata → 15 dk kilit; e-posta bazlı ayrı rate limit (token spam'ine karşı) |
| SQL injection | İncelenen ~16 ham SQL bloğunun tamamı parametreli; dinamik parçalar whitelist `switch` (`MetricToSql`, `PlanPredicate`) — enjeksiyon yüzeyi bulunamadı |
| Yetki (yazma) | Servis katmanında DB'den rol doğrulama; UI gizlemesine güvenilmiyor |
| Impersonation | Yalnız Admin; **başka bir Admin'e bürünme engelli**; başlangıç ve bitiş audit'e yazılıyor; banner ile görünür |
| Sır yönetimi | User Secrets (dev) + environment variable (prod); `SECURITY-SETUP.md` ile dokümante |
| HTTP header'ları | `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`; prod'da HSTS + HTTPS yönlendirme |
| Denetim | `AuditLogs` — parola değişimi, yayın, rollback, ayar değişikliği, impersonation |

### 5.4 Bulgular ve öneriler

| # | Bulgu | Etki | Öneri |
|---|---|---|---|
| G1 | **`AllowedHosts: "*"`** `appsettings.json`'da | Host header injection riski (prod'da env var ile override edilmesi bekleniyor) | Üretim değerinin gerçekten set edildiği doğrulanmalı; `appsettings.Production.json`'da yer tutucu bırakılmamalı |
| G2 | **CSP header'ı yok** | XSS savunma katmanı eksik | Blazor Server için `script-src 'self' 'unsafe-inline'` + `frame-ancestors 'none'` ile kısmi CSP yine de anlamlı koruma sağlar |
| G3 | **Okuma kapsamı UI'da belirleniyor** (§3.3-b) | Bugün sömürülebilir değil; gelecekte yanlış veri gösterimi riski | Kapsam türetmeyi servis imzasına taşı |
| G4 | **`LoginThrottle` bellek içi** | Restart kilitleri sıfırlar; çoklu sunucuda etkisiz | Kalıcılaştır (User tablosu) veya dağıtık cache |
| G5 | **Kullanıcı adı bazlı throttle (IP bazlı değil)** | Kullanıcı adı sayımı (user enumeration) yoluyla dağıtık deneme | IP bazlı ikinci sayaç ekle |
| G6 | **Exception mesajı UI'ye yansıtılıyor** (`Hesaplama hatası: {inner}`) | SQL/şema detayı ifşası | Kullanıcıya genel mesaj, ayrıntı yalnız loga |
| G7 | **Fotoğraflar DB'de `varbinary`** | DB büyümesi, yedek süresi; 5 MB × ST sayısı × dönem | Dosya sistemi veya blob storage + DB'de referans |
| G8 | **`Program.cs` başlangıçta `ALTER TABLE` çalıştırıyor** | Uygulama servis hesabının DDL yetkisi olmasını gerektirir | Şema değişimini dağıtım adımına taşı, uygulama hesabına yalnız DML yetkisi ver |
| G9 | **Bağlantı hedefi loglanıyor** (server + database adı) | Düşük — parola loglanmıyor, teşhis değeri yüksek | Kabul edilebilir; log erişimi kısıtlı tutulmalı |

---

## 6. Veri Bütünlüğü ve Denetlenebilirlik — En Kritik Bölüm

Bu sistem **para ödemesine dayanak** üretiyor. Bu nedenle aşağıdaki üç bulgu, teknik borçtan çok iş riski kategorisindedir.

### 6.1 (Yüksek) Yeniden hesaplama, yayınlanmış geçmişi geri dönüşsüz siliyor

`CalculateQuarterAsync` içinde (`CalculationService.cs:662-672`):

```csharp
var oldCalcs = await db.Calculations.Where(c => c.Year == year && c.Quarter == quarter).ToListAsync();
db.CalculationDetails.RemoveRange(oldDetails);
db.Calculations.RemoveRange(oldCalcs);
```

Sonuçlar:

- Yayınlanmış bir çeyrek yeniden hesaplanırsa **önceki prim tutarları hiçbir yerde saklanmaz**. `Calculation.IsFrozen = true` alanı var ama hiçbir yerde okunup silmeyi engellemiyor.
- Yeni satırlar `IsPublished = false` varsayılanıyla oluşur → **yeniden hesaplama sessizce yayını kaldırır**. ST'ler bir gün gördükleri sonucun ertesi gün kaybolduğunu görebilir.
- Audit log'a `Calculate` işlemi için kayıt düşmüyor (publish/rollback/ayar değişiklikleri düşüyor) — yani "kim, ne zaman, hangi sonucu ezdi" sorusu cevaplanamıyor.

**Öneri:** `Calculations` tablosuna `VersionNo` (veya `CalcRunId`) ekleyip silme yerine yeni versiyon yazmak; `IsPublished` olan versiyonu asla silmemek; `CalculateQuarterAsync`'i audit'e yazmak. Ek olarak, yayınlanmış bir çeyrek yeniden hesaplanmak üzereyken açık onay istemek.

### 6.2 (Yüksek) Geçmiş çeyrek sonuçları canlı brick haritasına bağımlı

Hedef, sell-out ve dağıtım skoru **o anki** `UserBricks` (`IsActive = 1`) durumundan hesaplanıyor. Bir brick 2026 Q3'te başka bir ST'ye devredilirse, Q2'nin yeniden hesaplanması **farklı bir sonuç** üretir. `db/brick-reassign-q2.sql` ve `brick-reassign-2026-07.sql` scriptlerinin varlığı, bu devirlerin gerçekten yaşandığını gösteriyor.

Sistem bu riski biliyor ve `CalcFreshnessBanner` ile uyarıyor — ama uyarı, dondurmanın yerini tutmaz.

**Öneri:** `UserBricks`'i tarih aralıklı (temporal) hâle getirip (`AssignedAt`/`UnassignedAt` alanları zaten var, sorgularda kullanılmıyor) hesaplamada çeyrek tarihine göre geçerli atamayı seçmek: `ub.AssignedAt <= quarterEnd AND (ub.UnassignedAt IS NULL OR ub.UnassignedAt > quarterStart)`. Bu, mevcut veri modeliyle uyumlu ve tek sorgu değişikliği gerektiren bir düzeltme.

### 6.3 (Orta) Strike Rate yokluğu skoru cezalandırıyor

`ScoreCalculator.StrikeScore` plan yoksa `null` döner ve UI'da "—" gösterilir. Ancak `FinalScore` hesabında:

```csharp
+ ((orderScore ?? 0m) * orderWeight)
```

Yani ziyaret planı yüklenmemiş bir ST **final skorunun %10'unu doğrudan kaybeder** — bu, ST'nin kontrolünde olmayan bir veri eksikliğinden kaynaklanan bir ceza. UI'da "—" gösterilirken skorda 0 sayılması ayrıca kafa karıştırıcıdır.

**Öneri:** Strike verisi yoksa ağırlığı diğer üç KPI'ya orantılı dağıtmak (yeniden normalize etmek) veya bu durumu açıkça "veri eksik, hesaplama geçersiz" olarak işaretlemek. Hangi davranışın doğru olduğu bir **iş kararı**dır — kodun bugünkü davranışı bilinçli mi, yoksa gözden kaçmış mı, iş sahibiyle netleştirilmeli.

---

## 7. Test Durumu

**111 test, 5 dosya, 1.272 satır — xUnit 2.9.3, coverlet ile kapsam toplama hazır.**

| Dosya | Test | Odak |
|---|---|---|
| `ImportValidationTests.cs` | 46 | Header doğrulama, alias ayrıştırma, ürün haritası, kültür-bağımsız sayı okuma, tutarlılık toleransı |
| `ScoreCalculatorTests.cs` | 41 | Cap, gate, kıdem, run-rate, kademe çarpanı, prim tutarı, dağıtım/strike skoru |
| `ExportServiceTests.cs` | 9 | Üretilen xlsx'in gerçekten açılabilirliği, sayfa sayısı, toplam formülü, boş liste dayanıklılığı |
| `AuthSecurityTests.cs` | 8 | Throttle davranışı, parola politikası, üretilen parolanın politikaya uyumu |
| `TaskServiceTests.cs` | 7 | Görev modülü |

### 7.1 Test aşamalarında fiilen ne yapılıyor

**a) Skor matematiği (`ScoreCalculatorTests`, 41 test)** — Saf fonksiyonlar sınır değerleriyle sınanıyor: hedef sıfırsa oran ve skor sıfır; tam ulaşımda ağırlık kadar skor; aşırı ulaşım %105'te cap'leniyor; **tam %105 cap'lenmiş sayılmıyor** (sınır davranışı açıkça test edilmiş). Ayrıca gate eşiği, kıdem hesabı (ayın ortasında işe giriş), run-rate oranı, kademe çarpanı seçimi ve prim tutarı yuvarlaması test ediliyor. Bu, paranın hesaplandığı çekirdeğin korunması demek.

**b) Import doğrulama (`ImportValidationTests`, 46 test)** — En kalabalık grup ve haklı olarak: veri okuma hataları sessizce yanlış prim üretir. Test edilenler:

- `ValidateHeader` — doğru başlık geçiyor, yanlış dosya reddediliyor, boş başlık satırı yakalanıyor, `minMatch` eşiği çalışıyor, **yeni sell-out düzeni geçiyor / eski düzen reddediliyor** (format geçişi regresyona karşı kilitlenmiş)
- `SplitAliases` — null/boş, tek değer, çoklu marka + ayraç/boşluk temizliği
- `BuildProductMap` — Name önceliği alias'ı ezemiyor, çoklu alias aynı ürüne gidiyor, pasif ürün listede yoksa alias kazanıyor
- `ParseCellNumber` — ham sayısal değer doğrudan kullanılıyor (binlik ayraç hatasının regresyon testi)
- `AmountsVerified` — tam eşitlik, küçük yuvarlama farkı, büyük fark, null güvenliği
- `ClassifyStatus` — skip/hata varsa `Partial`

**c) Export (`ExportServiceTests`, 9 test)** — Yalnız "patlamadı mı" bakılmıyor: üretilen byte dizisi **tekrar EPPlus ile açılıyor** ve sayfa sayısı, başlık konumu, kolon sayısı, ürün başına satır tekrarı, özet satırının doğru kolonlarda olması ve **toplam formülünün varlığı** doğrulanıyor. Boş liste senaryosu da ayrıca test edilmiş.

**d) Güvenlik (`AuthSecurityTests`, 8 test)** — Beş denemede kilit, dördüncüde kilitlenmeme, reset'in kilidi kaldırması, kullanıcı adının büyük/küçük harf duyarsızlığı, farklı kullanıcıların birbirinden bağımsızlığı; parola politikası geçerli/geçersiz örneklerle (`[Theory]`), ve **üretilen varsayılan parolanın kendi politikasına uyduğu**.

**e) Görev modülü (`TaskServiceTests`, 7 test)** — Atama ve durum geçişi kuralları.

### 7.2 Test aşamalarında yapılmayanlar

| Aşama | Durum |
|---|---|
| Birim testi (saf mantık) | ✅ Var, iyi |
| Birim testi (orkestrasyon) | ❌ `CalculateQuarterAsync` hiç test edilmemiş |
| Entegrasyon testi (DB) | ❌ Yok — InMemory provider veya Testcontainers kullanılmıyor |
| Import uçtan uca testi | ❌ Yok — yalnız yardımcıları test ediliyor, 8 akışın kendisi değil |
| UI / bileşen testi | ❌ Yok — bUnit yok, 23 sayfa test dışı |
| Yetki testi | ❌ Yok — "ST başkasının verisini göremiyor" iddiası test edilmiyor |
| Yük / performans testi | ❌ Yok — 111 bin satırlık import süresi ölçülmüyor |
| Regresyon otomasyonu (CI) | ❌ Yok |
| Manuel test prosedürü | ❌ Yazılı bir test planı/checklist bulunamadı |
| Kapsam ölçümü | ⚠️ `coverlet.collector` ekli ama raporlandığına dair iz yok |

**En kritik boşluk yetki testleridir.** Sistem prim tutarı gösteriyor; "ST A, ST B'nin primini göremez" kuralı bugün yalnız UI'nın doğru parametre geçirmesine dayanıyor (§5.1) ve bunu doğrulayan tek bir test yok. Bu, yazılması en kolay ve getirisi en yüksek test grubudur.

### 7.3 Güçlü yön ve boşluk özeti

**Güçlü yön:** Testler *doğru yeri* hedefliyor — para hesabını belirleyen saf fonksiyonlar ve veri kaybına yol açabilecek parse/doğrulama mantığı. `InternalsVisibleTo` ile `internal` yardımcılar test edilebilir kılınmış. Export testleri çıktının gerçekten geçerli bir xlsx olduğunu doğruluyor (yalnız "patlamadı" demiyor).

**Boşluklar:**

1. **`CalculateQuarterAsync` hiç test edilmemiş.** Sistemin en kritik metodu (450 satır, DB'ye bağımlı) test dışı. `ScoreCalculator` test edilmiş ama onu *çağıran orkestrasyon* — ağırlık seçimi, `SharePercent` çarpanı, transaction, run-rate uygulaması — değil.
2. **Import metodlarının kendisi test edilmemiş** — yalnız yardımcıları. 8 × ~250 satırlık akış test dışı.
3. **DB entegrasyon testi yok.** `Microsoft.EntityFrameworkCore.InMemory` veya Testcontainers ile SQL Server üzerinde uçtan uca hesaplama testi mümkün ve yüksek değerli olurdu.
4. **UI/bileşen testi yok** (bUnit). 23 sayfa, ~8.000 satır Razor kodu test dışı.
5. **Test projesi `PrimSystem.slnx`'e ekli değil** — `<Solution><Project Path="PrimSystem/PrimSystem.csproj" /></Solution>`. Yani `dotnet build`/`dotnet test` çözüm üzerinden çalıştırıldığında testler **hiç derlenmiyor ve koşmuyor**. Bu, tek satırlık bir düzeltmeyle giderilebilecek ama etkisi büyük bir eksik.

---

## 8. Operasyonel Olgunluk

| Konu | Durum |
|---|---|
| **Sürüm kontrolü** | **Git deposu yok** — `.git` ve `.gitignore` bulunmuyor. Değişiklik geçmişi, dallanma, geri alma imkânı yok |
| **CI/CD** | Yok |
| **Loglama** | Serilog — konsol + günlük döngülü dosya, 30 gün saklama, istek loglama. İyi |
| **İzleme (monitoring)** | Health check endpoint'i, metrik, APM yok |
| **Yapı artefaktları** | `bin/`, `obj/`, `.vs/` klasörleri çalışma dizininde; log dosyaları `bin/Debug/net8.0/logs/` altında |
| **Hassas veri** | Kök dizinde gerçek şirket verisi içeren Excel'ler (`Kenvue_Q2_vs_SellOut_N_St_Brick.xlsx`, `Mutabakat_26.xlsx` — 1,7 MB, `Prim_2026_Q2_20260616.xlsx`). Git eklenirse bunların `.gitignore`'a girmesi zorunlu |
| **Lisans** | **EPPlus `LicenseContext.NonCommercial`** — kurumsal/ticari kullanımda EPPlus ticari lisansı gerekir. Alternatif: ClosedXML veya OpenXML SDK. Bu bir **hukuki risk**, teknik değil |
| **Yatay ölçekleme** | Bellek içi `SessionStore` + `LoginThrottle` → sticky session zorunlu; Blazor Server circuit'i doğal olarak sunucuya bağlı |
| **Yerelleştirme** | Türkçe sabit metin (hard-coded) — çok dillilik gerekirse kapsamlı refactor |

---

## 9. Kod Kalitesi

### 9.1 Örnek nitelikte olan yön: yorumlar

Bu kod tabanının en ayırt edici özelliği yorum kalitesi. Örnekler:

> "Önceki kod `.Text` (biçimli metin) okuyup naïf `Replace(",",".")` + Invariant kullandığından '1.538' → 1,538 gibi ≥1000 değerleri bozuyordu; bu yardımcı onu giderir."

> "ub JOIN'i yalnız sipariş kendi UserId'sini taşımıyorsa devreye girer (o.UserId IS NULL) — aksi halde satır çoğalıp SUM'ı iki katına çıkarırdı."

> "Neden VisitPlans'tan ayrı: (a) VisitPlans Strike Rate KPI'ının girdisi — oraya yazmak skor ekranını etkiler; (b) Devir nedeniyle aynı GLN iki ST'ye ait olabiliyor (dosyada 202 örnek), tek bit bunu temsil edemez."

Bunlar **kurumsal hafıza**dır: hangi hatanın yaşandığı, hangi alternatifin neden reddedildiği, veri gerçekliğinin ne olduğu. Bu tür yorumları bulmak nadirdir ve devir teslimde paha biçilmez değerdedir.

### 9.2 Sorunlu yön: sunum katmanı

Tüm stil **inline** yazılmış. Tek bir satır örneği (`Home.razor`):

```razor
<div style="font-size:15px;font-weight:700;color:#f1f5f9;margin-bottom:2px">@volumeStar.FullName</div>
```

`wwwroot/app.css` yalnızca 274 satır ve çoğu 3D kart animasyonu. Sonuçlar:

- **Renk paleti kod içine gömülü** — `#f1f5f9`, `#0b1120`, `rgba(16,185,129,0.12)` yüzlerce yerde tekrarlanıyor. Tema değişikliği veya kurumsal renk güncellemesi tüm Razor dosyalarında arama-değiştirme gerektirir.
- **Razor dosyaları şişiyor** — `Scorecard.razor` 1.081 satır, `Home.razor` 971 satır; büyük kısmı stil metni. Mantığı bulmak zorlaşıyor.
- **Erişilebilirlik** — kontrast, klavye navigasyonu, ARIA etiketleri gözden geçirilmemiş görünüyor. Emoji ikonlar (`🏆`, `⚙️`) ekran okuyucular için etiketsiz.

**Öneri:** CSS değişkenleriyle (`--color-bg`, `--color-accent`) tek bir tema katmanı + tekrarlayan bileşenler için utility sınıfları. Bu, davranışı hiç değiştirmeden yapılabilecek, en yüksek getirili bakım iyileştirmesi.

### 9.3 Diğer gözlemler

- Nullable reference types açık (`<Nullable>enable</Nullable>`) — iyi.
- Tutarlı `async/await`; senkron blokaj (`.Result`, `.Wait()`) görülmedi.
- Sessiz `catch { }` blokları var (`MergeNameAliasesAsync`, `StarCriteria` okuma) — "tablo yoksa devam et" mantığı için kasıtlı ve yorumlanmış, ama gerçek bir hatayı da yutabilir. Log'a `Debug` seviyesinde yazmak daha güvenli olurdu.
- `AuditService` hatayı yutup logluyor — doğru karar (audit yazımı ana işlemi düşürmemeli).
- Sekiz import metodundaki iskelet tekrarı ve `CalculationService`'teki 17 analitik metodun benzer ADO.NET boilerplate'i (bağlantı aç → cmd kur → parametre döngüsü → reader → kapat) ortak bir yardımcıya çıkarılabilir; tahminen 300–500 satır azalma sağlar.

---

## 10. Artılar / Eksiler Özeti

### Artılar

1. **Domain modellemesi olgun** — brick paylaşımı (`SharePercent`), GLN kırılımı, geçmişe dönük ağırlık versiyonlama, kıdem eşiği, iade davranışı, cap kuralları: hepsi gerçek iş gereksinimlerinden türemiş.
2. **Skor matematiği izole ve test edilmiş** — `ScoreCalculator` saf, statik, 41 testle korunuyor.
3. **Import hattı savunmacı** — dry-run, hash, header doğrulama, ad bazlı kolon haritası, kültür-bağımsız parse, eşleşme raporu, insan onaylı bulanık eşleme, atomik rollback, ham dosya arşivi.
4. **SQL enjeksiyonu bulunamadı** — tüm dinamik SQL parametreli, whitelist'li.
5. **Güvenlik temelleri doğru** — BCrypt, hash'li reset token, throceling, impersonation kısıtı ve audit'i, sır yönetimi dokümanı.
6. **Performans bilinçli** — kapsayıcı indeksler, `OPTION (RECOMPILE)`, ADO.NET, açık timeout'lar, brick özeti ile 27k satırlık payload'dan kaçınma, EF komut logunun susturulması.
7. **Yorum kalitesi olağandışı** — kararların gerekçeleri ve geçmiş hatalar kod içinde belgelenmiş.
8. **Yayın iş akışı gerçekçi** — hesapla → incele → yayınla → beklet, freshness uyarısıyla birlikte.
9. **Excel çıktıları kullanılabilir** — gerçek sayı/formül, filtre, dondurma, yazdırma ayarları.
10. **`AddDbContextFactory` doğru kullanımı** — Blazor Server'ın en yaygın hatası yapılmamış.
11. **Toplu yazma zaten optimize** — 106 bin `SellOutDetail` satırı `SqlBulkCopy` ile aynı transaction'da yazılıyor (~20 sn → ~1–2 sn); `Institutions` upsert'i geçici tablo + tek `MERGE` ile set-temelli. Dış değerlendirmenin "tek tek insert var" tespitinin aksine bu yol zaten çözülmüş.

### Eksiler

| Öncelik | Konu |
|---|---|
| **Kritik** | Yeniden hesaplama, yayınlanmış prim geçmişini geri dönüşsüz siliyor ve yayını sessizce kaldırıyor (§6.1) |
| **Kritik** | Geçmiş çeyrek sonuçları canlı brick haritasına bağımlı — brick devri geçmişi değiştiriyor (§6.2) |
| **Kritik** | Git deposu yok — değişiklik geçmişi ve geri alma imkânı yok (§8) |
| **Yüksek** | **Girdi verisi donduruumuyor** — `ConfigSnapshot` yapılandırmayı saklıyor ama hesaplamaya giren hedef/sell-out/atama verisinin hangi hâli kullanıldığı kayıtlı değil (`DataHash` + `ImportBatchId` bağı önerildi — §12.1/E1, E2) |
| **Yüksek** | `CalculateQuarterAsync` ve import akışları test dışı; test projesi çözüme ekli değil (§7) |
| **Yüksek** | EPPlus NonCommercial lisansı — ticari kullanımda hukuki risk (§8) |
| **Yüksek** | Strike Rate yokluğu skorun %10'unu cezalandırıyor — iş kuralı netleştirilmeli (§6.3) |
| **Yüksek** | **`MinRate` ölü yapılandırma** — ürün ve KPI bazında girilip kaydediliyor, skorda hiç kullanılmıyor (§3.4.2) |
| **Yüksek** | **Hesaplama işlemi audit'e yazılmıyor** (`Calculate` aksiyonu yok) — prim değiştiren en önemli işlem izsiz (§5.2/D1) |
| **Orta** | **Ağırlık toplamı doğrulanmıyor** — ürün ağırlıkları için yalnız görsel rozet, KPI ağırlıkları için hiçbir kontrol yok (§3.4.2, §3.4.5) |
| **Orta** | **Giriş/çıkış ve başarısız denemeler audit'e yazılmıyor** (§5.2/D2) |
| **Orta** | **Audit görüntüleme ekranı yok** — kayıtlara yalnız SQL ile ulaşılıyor (§5.2/D6) |
| **Orta** | **Chart.js dış CDN'den** — kapalı ağda tüm grafikler sessizce boş kalır (§4.8.4/W1) |
| **Orta** | **Yetki testi yok** — "ST başkasının primini göremez" kuralı test edilmiyor (§7.2) |
| **Orta** | **Health check / izleme yok**, SignalR circuit limitleri varsayılan (§4.8.4/W2, W3) |
| **Orta** | **ST'de geçmiş dönem karşılaştırması ve simülasyon yok**; mobil uyum test edilmemiş (§4.7.5) |
| **Orta** | `CalculationService` (2.107) ve `ImportService` (2.492) tanrı-servis; sorumluluk ayrımı gerekli (§3.3-a) |
| **Orta** | Okuma kapsamı UI'da belirleniyor — servis imzasına taşınmalı (§3.3-b) |
| **Orta** | Şema yönetiminde üç paralel yol (migration + script + çalışma zamanı self-heal) (§3.3-c) |
| **Orta** | Tüm stil inline; tema/bakım maliyeti yüksek (§9.2) |
| **Orta** | Fotoğraflar DB'de `varbinary` — yedek ve büyüme sorunu (G7) |
| **Orta** | CI/CD, health check, izleme yok (§8) |
| **Düşük** | CSP yok; `AllowedHosts: "*"`; exception mesajı UI'ye sızıyor; throttle bellek içi ve IP bazlı değil (§5.2) |
| **Düşük** | Bellek içi oturum → yatay ölçekleme kısıtı; Türkçe metinler kodda sabit |
| **Düşük** | ADO.NET boilerplate ve import iskeleti tekrarı (§9.3) |

---

## 11. Master Plan — Öncelikli Yol Haritası

> **Tahmini efor.** Dış rapordan alınan adam-gün tahminleri her faz başlığına eklendi. Bu rakamlar **tek geliştirici, kesintisiz çalışma** varsayar; kod incelemesi, iş sahibi onay turları ve üretim verisi üzerinde göç provası dahil değildir. Gerçekçi takvim için ~1,5–2 kat pay bırakılmalıdır.

### Faz 0 — Emniyet Ağı (1 hafta · ~2 adam-gün)

Hiçbir işlevsel değişiklik yapmadan, geri dönüşü olmayan hataları engelle.

| # | İş | Kabul kriteri |
|---|---|---|
| 0.1 | Git deposu başlat; `.gitignore` (bin, obj, .vs, logs, *.xlsx) ile ilk commit | `git log` çalışıyor, hassas Excel'ler ve artefaktlar takip dışı |
| 0.2 | Test projesini `PrimSystem.slnx`'e ekle | `dotnet test` çözüm üzerinden 111 testi koşuyor |
| 0.3 | DB yedekleme ve geri yükleme prosedürünü yaz ve **bir kez test et** | Yedekten geri dönüş süresi ölçülmüş |
| 0.4 | Kritik iş kurallarını iş sahibiyle doğrula: Strike Rate yokluğu, **`MinRate` ne olmalı**, %105 cap, kıdem eşiği, run-rate modu kimin kararı | Yazılı onay dokümanı |
| 0.5 | Chart.js'i `wwwroot`'a indir (CDN bağımlılığını kaldır) | Kapalı ağda grafikler çalışıyor |
| 0.6 | `Calculate` + `Login`/`LoginFailed` aksiyonlarını audit'e ekle | `AuditLogs`'ta hesaplama ve giriş izi var |

### Faz 1 — Veri Bütünlüğü (2–3 hafta · ~5 adam-gün) — *en yüksek iş değeri*

| # | İş | Kabul kriteri |
|---|---|---|
| 1.1 | `Calculations`'a `VersionNo`/`CalcRunId` ekle; yeniden hesaplama silmek yerine yeni versiyon yazsın | Yayınlanmış versiyon hiçbir koşulda silinmiyor |
| 1.2 | Yayınlanmış çeyreği yeniden hesaplamada açık onay + audit kaydı | `AuditLogs`'ta `Calculate` işlemi görünüyor |
| 1.3 | `UserBricks`'i tarih aralıklı sorgula (`AssignedAt`/`UnassignedAt`) — hesaplama çeyrek tarihine göre geçerli atamayı kullansın | Geçmiş çeyrek iki kez hesaplandığında aynı sonuç çıkıyor |
| 1.4 | Strike Rate kararını uygula (yeniden normalize veya "veri eksik" işareti) | Faz 0.4'teki karara uygun; test ile korunuyor |
| 1.5 | `CalculateQuarterAsync` için entegrasyon testi — **Testcontainers + gerçek SQL Server** (EF InMemory ham SQL ve transaction desteklemediğinden bu projede kullanılamaz) | Altın senaryolar 1–3, 5, 7 yeşil |
| 1.6 | **`MinRate` kararını uygula** — ya iş kuralına bağla ya ekrandan kaldır | Ekranda görünen her alan skoru etkiliyor |
| 1.7 | **Ağırlık toplamı doğrulaması** — ürün ağırlıkları ve KPI ağırlıkları 100 değilse kaydı bloke et veya onay iste | Altın senaryo 9 yeşil |
| 1.8 | **`CalculationRun.DataHash`** — hesaplama girdisinin SHA256 özeti run ile saklansın *(E1)* | İki çalıştırma arasında girdi değişti mi, tek alan karşılaştırmasıyla belli |
| 1.9 | **Girdi–run ilişkisi** — kullanılan `ImportBatchId`'ler run'a bağlansın *(E2, hafif sürüm: satır kopyalama değil referans)* | "Bu prim hangi dosyalarla hesaplandı" DB'den cevaplanıyor |
| 1.10 | **Hedefsiz ürün kararı** — üç seçenek (bugünkü ceza / tam kredi / paydadan çıkarma) iş sahibine sunulup biri seçilsin *(§12.3-c)* | Altın senaryo 4 tanımlı ve test edilmiş |

### Faz 2 — Test ve Otomasyon (2–3 hafta · ~4 adam-gün)

| # | İş |
|---|---|
| 2.1 | Import akışları için entegrasyon testleri — **Testcontainers + gerçek SQL Server**, küçük gerçek Excel örnekleriyle, her tip için en az bir mutlu yol + bir hata yolu |
| 2.2 | CI kur (GitHub Actions / Azure DevOps): build + test + kapsam raporu her commit'te; **test geçmezse dağıtım bloke edilsin** *(E8)* |
| 2.3 | Kritik sayfalar için bUnit testleri (Login, Reports kapsam filtresi, Calculate) |
| 2.4 | Health check endpoint'i (`/health` — DB bağlantısı dahil) + yapılandırılmış hata sayaçları |
| 2.5 | **Yetki testleri** — §12.4'teki Y1–Y6 senaryoları (en yüksek getirili test grubu) |
| 2.6 | **Audit görüntüleme ekranı** — filtreli, sayfalı denetim kaydı sayfası (Admin) + saklama politikası |
| 2.7 | **Kullanıcı CRUD + başarısız giriş audit'i**; başarısız denemeler bellekten DB'ye taşınsın *(E11)* |
| 2.8 | **Altın senaryo 6 ve 10** için entegrasyon testi (brick paylaşımı, tarihsel ağırlık seçimi) |

### Faz 3 — Mimari Sağlamlaştırma (3–4 hafta · ~6 adam-gün)

| # | İş |
|---|---|
| 3.1 | `CalculationService`'i üçe ayır: `PrimCalculationService` (motor), `PublicationService` (yayın/leaderboard), `AnalyticsService` (17 sorgu) |
| 3.2 | Import'u tip başına sınıfa ayır: ortak `ImportPipeline` tabanı + 8 `IImporter` uygulaması |
| 3.3 | Okuma kapsamını servis imzasına taşı — `actorUserId`'den kapsam türet; UI'nın filtre geçirmesini gereksiz kıl |
| 3.4 | ADO.NET boilerplate'i tek yardımcıya indir (`QueryAsync(sql, parameters, rowMapper)`) |
| 3.5 | Şemayı tek yola yakınsat: `db/*.sql` içeriğini migration'lara taşı, `Program.cs` self-heal bloğunu kaldır; **uygulama hesabına yalnız DML, migration'lar pipeline'da ayrı admin hesapla** *(E12)* |
| 3.6 | **DB şema dokümantasyon belgesi** — tüm tablolar, alanlar ve yeni `CalculationRun` yapısı *(E14)* |
| 3.7 | **Statik veri önbelleği** — ürün/brick haritaları için çağrılar arası `IMemoryCache` *(E9)* |

### Faz 4 — Sunum ve Erişilebilirlik (2–3 hafta · ~3 adam-gün)

| # | İş |
|---|---|
| 4.1 | CSS değişkenleriyle tema katmanı; inline renkleri kademeli olarak taşı (sayfa sayfa, davranış değişmeden) |
| 4.2 | Tekrarlayan UI parçalarını bileşene çıkar (stat kartı, tablo başlığı, badge) — Razor dosyalarını %30–40 küçültme hedefi |
| 4.3 | Erişilebilirlik geçişi: kontrast oranları, emoji ikonlara `aria-label`, klavye navigasyonu, form etiketleri |
| 4.4 | Yüklenme/hata durumlarını standartlaştır (`GlobalLoading` + tek hata bileşeni); **ST'ye yayın yokken "Prim sonuçları hazırlanıyor" boş durumu** *(E13)* |
| 4.5 | **"Veri Kalitesi Skoru"** — import'ta zaten toplanan metrikler (eşleşme oranı, atlanan satır, doğrulama bayrağı) tek skora çevrilip hesaplama ve ST ekranında gösterilsin *(E10)* |

### Faz 5 — Operasyon ve Güvenlik Cilası (1–2 hafta · ~2 adam-gün)

| # | İş |
|---|---|
| 5.1 | EPPlus lisans kararı: **lisans bedeli ile ~600 satırlık ClosedXML göçünün geliştirici maliyeti karşılaştırılsın** (§12.5) |
| 5.2 | Fotoğrafları DB dışına taşı (dosya sistemi/blob + DB referansı) |
| 5.3 | CSP header'ı ekle — **Chart.js yerelleştirildikten SONRA**, `script-src 'self' 'unsafe-inline'` ile (Blazor inline script gerektirir; `'self'` tek başına uygulamayı açılmaz hâle getirir — §12.3-b); `AllowedHosts` üretim değerini doğrula; exception detayını UI'dan kaldır |
| 5.4 | `LoginThrottle`'ı kalıcılaştır + IP bazlı ikinci sayaç |
| 5.5 | Dağıtım runbook'u: ortam değişkenleri, migration adımı, geri alma (rollback) planı |
| 5.6 | SignalR circuit limitlerini açıkça ayarla; bağlantı kopma overlay'ini Türkçeleştir |
| 5.7 | Sticky session yapılandırmasını doğrula/dokümante et. **Çok sunuculuya geçilecekse Azure SignalR Service sticky gereksinimini tamamen kaldırır** — Redis backplane ise sticky ile birlikte kullanılmalıdır *(E5)* |
| 5.8 | APM/izleme (Application Insights, Prometheus vb.) — SQL sorgu süreleri ve exception sayıları |

### Faz 6 — Genişleme (opsiyonel, iş talebine bağlı)

- Prim itiraz/onay iş akışı (ST itiraz eder → BM görüş verir → Admin karara bağlar), tam audit izli
- ST'ye kendi skorunun "ne olursa ne olur" simülasyonu (hedefe X kutu daha satarsam skorum ne olur) — veri zaten ekranda, hesap istemci tarafında yapılabilir
- ST panosuna geçmiş dönem karşılaştırması (çeyreklik trend çizgisi) — "gelişiyor muyum" sorusunun cevabı
- Mobil/responsive düzen — saha ekibinin telefondan bakacağı varsayımıyla
- Otomatik dönemsel rapor e-postası (mevcut `EmailService` altyapısı hazır)
- Çok yıllık trend analizi ve ST bazlı gelişim grafiği
- Yatay ölçekleme gerekirse: dağıtık oturum (Redis) + sticky session yapılandırması

---

## 12. Dış Değerlendirme Raporunun İncelenmesi ("Yönetici Özeti", 17 sayfa)

İkinci bir değerlendirme raporu sunuldu. Genel yönü bu raporla **örtüşüyor** — veri değişmezliği, audit ve yetki kırmızı risk olarak öne çıkarılmış. Aşağıda madde madde ayıklandı: hangileri haklı ve eklendi, hangileri kodla çelişiyor.

### 12.1 Haklı bulunan ve yol haritasına eklenenler

| # | Öneri | Neden değerli |
|---|---|---|
| **E1** | **`CalculationRun.DataHash`** — hesaplamaya giren verinin SHA256 özeti | Benim önerdiğim `VersionNo`'nun üstüne çok şey katıyor: iki çalıştırma arasında **girdinin değişip değişmediği** tek alan karşılaştırmasıyla anlaşılır. "Aynı veriyle mi tekrar hesapladık, yoksa veri mi değişti" sorusu böylece kanıtlanabilir olur |
| **E2** | **Girdi verisi snapshot'ı** — kullanılan hedef/sell-out/atama satırlarının run ile ilişkilendirilmesi | `ConfigSnapshot` bugün yalnız *yapılandırmayı* donduruyor; *veriyi* dondurmuyor. Tam denetlenebilirlik için doğru yön. **Uyarı:** tüm satırların kopyalanması DB'yi şişirir; pragmatik yol `ImportBatchId` referansı + `DataHash` |
| **E3** | **Testcontainers'ı InMemory'ye tercih et** | Bu, benim Faz 1.5'imde "InMemory **veya** Testcontainers" diye bıraktığım seçimi netleştiriyor ve haklı: EF InMemory sağlayıcısı raw SQL ve transaction desteklemiyor — bu proje **ham ADO.NET sorgularıyla dolu**, InMemory ile test edilemezler. Faz 1.5 ve 2.1 güncellendi |
| **E4** | **"10 Altın Senaryo" test tablosu** | Somut, doğrudan koda çevrilebilir kabul kriterleri. §12.4'e alındı |
| **E5** | **Azure SignalR Service alternatifi** | Sticky session zorunluluğunu **tamamen kaldıran** tek seçenek. Ölçekleme tartışmasında eksik bıraktığım bir yol |
| **E6** | **Excel kütüphanesi karşılaştırma tablosu** | EPPlus lisans kararına somut alternatif seti sunuyor (ClosedXML / OpenXML SDK / NPOI). §12.5'e alındı |
| **E7** | **Adam-gün tahminleri** (Faz 0: 2, Faz 1: 5, Faz 2: 4, Faz 3: 6, Faz 4: 3, Faz 5: 2 ≈ 22 gün) | Yol haritama eksik olan planlama boyutunu ekliyor. **Uyarı:** bu rakamlar tek geliştirici ve kesintisiz çalışma varsayar; kod incelemesi, test yazımı ve iş sahibi onay turları hesaba katılmamış — gerçekçi takvim ~1,5–2 katı |
| **E8** | **CI pipeline'ının dağıtımı bloke etmesi** | "Test geçmezse deploy engellenmeli" — benim CI önerimde açıkça yazmadığım kritik detay |
| **E9** | **Statik veri önbelleği** (ürün listesi, brick eşlemeleri) | Import'ta `userBrickMap` zaten belleğe alınıyor; aynı desenin ürün/brick haritaları için de **çağrılar arası** (IMemoryCache) uygulanması makul |
| **E10** | **"Data Quality Score"** — her hesaplamada veri kalitesi göstergesi | Import'ta zaten toplanan metrikler (eşleşme oranı, atlanan satır, doğrulama bayrağı) tek bir skora çevrilip ST'ye ve admin'e gösterilebilir. Ucuz ve etkili |
| **E11** | **Kullanıcı CRUD audit'i** ve **başarısız girişin DB'ye yazılması** | Benim D2/D3 bulgularımla aynı; öncelik teyidi |
| **E12** | **Uygulama DB hesabına yalnız DML yetkisi; migration'ı ayrı admin hesapla pipeline'da çalıştır** | Benim W6 bulgumu somut bir operasyonel modele bağlıyor |
| **E13** | **Boş durum iyileştirmesi** ("Prim sonuçları hazırlanıyor") | §4.7.5'te tespit ettiğim boşluğun çözümü |
| **E14** | **DB şema dokümantasyon belgesi** | Yeni tabloların ve alanların merkezi dokümantasyonu — devir teslim için değerli |

### 12.2 Kodla çelişen iddialar — düzeltme

Aşağıdaki dört madde kod tabanıyla **doğrudan çelişiyor**. Bunlara göre iş planlanırsa gereksiz efor harcanır.

| İddia (PDF) | Gerçek | Kanıt |
|---|---|---|
| *"Impersonation dönüşler loglanmıyor; `ImpersonateStop` audit'e yazılmalı. Aktif impersonasyon bir banner ile gösterilmeli."* | **Her ikisi de zaten var.** `ImpersonateStart` ve `ImpersonateStop` audit'e yazılıyor; `ImpersonationBanner` bileşeni `MainLayout`'ta koşulsuz render ediliyor | `SessionState.cs:130,145` · `MainLayout.razor:16` |
| *"Şimdilik bir transaction içinde tek tek insert var. Bunun yerine SqlBulkCopy veya TVP kullanılabilir."* | **En ağır yolda `SqlBulkCopy` zaten kullanılıyor** — 106 bin `SellOutDetail` satırı için, aynı transaction içinde; kod yorumu kazancı "~20 sn → ~1–2 sn" olarak belgeliyor. Ayrıca `Institutions` upsert'i geçici tablo + tek `MERGE` ile set-temelli yapılıyor | `ImportService.cs:212, 238, 266-299, 950` |
| *"EPPlus 5 sürümü artık ticari lisanslıdır"* | Proje **EPPlus 7.5.2** kullanıyor. Lisans uyarısının kendisi geçerli, sürüm referansı yanlış | `PrimSystem.csproj:22` |
| *"Cap kuralı negatiflere uygulanmaz"* | `Math.Clamp(rate, 0m, 1.05m)` **her iki sınırı da** uygular; negatif oran 0'a tabanlanır. Davranış doğru anlatılmış ama gerekçe yanlış ifade edilmiş | `ScoreCalculator.cs:24` |

### 12.3 Dikkatle ele alınması gerekenler

**(a) Migration SQL'i olduğu gibi çalıştırılamaz.** PDF'teki 1. adım:

```sql
ALTER TABLE Calculations ADD RunId INT IDENTITY PRIMARY KEY;
```

Bu ifade geçersizdir — `Calculations` tablosunda zaten `CalcId` birincil anahtarı var; bir tabloya ikinci PK eklenemez ve mevcut satırları olan bir tabloya bu biçimde IDENTITY + PK kolonu eklenemez. SQL blokları **kavramsal taslak** olarak okunmalı, üretimde çalıştırılmamalı. Doğru yol: yeni `CalculationRun` tablosunu oluşturup veriyi taşımak, eski tabloyu sonra kaldırmak.

**(b) CSP örneği Blazor Server'ı kırar.** Önerilen header:

```
default-src 'self'; script-src 'self';
```

Blazor Server başlangıç script'lerini satır içi (inline) enjekte eder; `'unsafe-inline'` olmadan uygulama **hiç açılmaz**. Ayrıca Chart.js CDN'den geldiği sürece `cdn.jsdelivr.net` de izinli olmalı (veya önce yerelleştirilmeli). Doğru sıralama: **önce Chart.js'i yerelleştir, sonra** `script-src 'self' 'unsafe-inline'` ile CSP ekle ve test et.

**(c) "Hedefsiz ürün için Rate=1 kabul et" önerisi risklidir.** PDF'in 4. senaryosu, hedefi olmayan ürüne tam kredi verilmesini seçenek olarak sunuyor. Bu, hedef girilmeyen ürünlerin **bedava puan** üretmesine yol açar ve kötüye kullanıma açıktır. Daha savunulabilir alternatif: hedefsiz ürünü **ağırlıklı paydadan çıkarmak** (kalan ürünleri yeniden normalize etmek) — böylece ne ceza ne bedava puan olur. Bu yine de bir **iş kararıdır**; üç seçenek (bugünkü ceza / tam kredi / yeniden normalize) iş sahibine birlikte sunulmalı.

**(d) `ExcelQueryFactory` önerisi geçersiz.** Bu, uzun süredir bakımı yapılmayan LinqToExcel kütüphanesine ait; bugünün alternatifi değil. `ExcelDataReader` (yalnız okuma) ve `ClosedXML` (okuma+yazma) geçerli seçenekler.

**(e) Takvim iyimser.** PDF'teki zaman çizelgesi 31 Temmuz – 3 Eylül aralığına (≈5 hafta) altı fazın tamamını sığdırıyor. 22 adam-günlük tahmin tek geliştirici için teorik olarak tutar; ancak Faz 1'deki veri modeli göçü **üretim verisi üzerinde** yapılacak — göç provası, doğrulama ve geri alma planı bu tahmine dahil değil. Faz 0–1'i (7 gün) korumak, sonrasını iş sahibi önceliğine bırakmak daha gerçekçi.

### 12.4 "10 Altın Senaryo" — Kabul Kriteri Tablosu

Bu tablo doğrudan test kodu iskeletine çevrilebilir. Sağ sütun, benim kod incelememle **doğrulanmış** beklenen davranışı içerir.

| # | Senaryo | Girdi | Beklenen (kod ile doğrulanmış) | Test tipi |
|---|---|---|---|---|
| 1 | Tam hedef | Tüm ürünlerde `Actual = Target` | `salesScore = 1.00`; ağırlık toplamı 100 ise final katkısı 0,60; gate diğer KPI'lara bağlı | Birim |
| 2 | %105 aşımı | Bir üründe `Actual = 2 × Target` | `Rate = 2.00`, `CappedRate = 1.05`, `IsCapped = true`; UI'da `↑` | Birim ✅ *(mevcut)* |
| 3 | Yarım ulaşım | `Actual = 0.5 × Target` | Katkı ağırlığın yarısı | Birim ✅ *(mevcut)* |
| 4 | **Hedefsiz ürün** | `Target = 0`, `Actual > 0` | **Bugün:** `Rate = 0` → ağırlık tümüyle ceza. **Karar bekliyor** (§12.3-c) | Birim + iş kararı |
| 5 | İade / negatif | `Actual = -10`, `Target = 100` | `Rate = -0.10` → `CappedRate = 0`; diğer ürünlerden puan **götürmez** | Birim ✅ *(mevcut)* |
| 6 | **Brick paylaşımı** | Brick %70 ST-A / %30 ST-B | Hedef **ve** gerçekleşen `SharePercent` ile çarpılır; sipariş yalnız `o.UserId IS NULL` iken paylaştırılır (çift sayım guard'ı) | **Entegrasyon — eksik** |
| 7 | Kısmi dönem | 3 aylık çeyrekte 2 ay veri, `RunRateMode` açık | `soldRatio = 0,667`; hedef orantılanır | Birim ✅ *(mevcut)* |
| 8 | Strike verisi yok | Ziyaret planı boş | **Bugün:** `null` → final'de 0 sayılır, UI'da "—". Kod yorumu "yeniden ağırlıklandırılır" diyor ama **yapılmıyor** (§3.4.7) | Birim + iş kararı |
| 9 | **Ağırlık toplamı ≠ 100** | Ürün ağırlıkları 95, KPI ağırlıkları 105 | **Bugün:** ürün için yalnız görsel rozet, KPI için **hiçbir kontrol yok** → kayıt geçer, skorlar sapar | **Eksik — doğrulama eklenmeli** |
| 10 | Tarihsel ağırlık | Farklı `ValidFrom` değerli iki ağırlık versiyonu | `ValidFrom <= quarterStart` olan seçilir; eşitlikte `max WeightId` (deterministik) | **Entegrasyon — eksik** |

Ek olarak **yetki senaryoları** (PDF'te ayrıca vurgulanmış, katılıyorum):

| # | Senaryo | Beklenen |
|---|---|---|
| Y1 | ST kendi raporunu ister | Yalnız kendi satırı; `publishedOnly` zorunlu |
| Y2 | ST yayınlanmamış dönemi ister | Hiçbir kayıt dönmez |
| Y3 | BM rapor ister | Yalnız kendi bölgesindeki ST'ler |
| Y4 | BM başka bölgenin ST'sine görev atar | Reddedilir |
| Y5 | ST ham hesaplama dışa aktarımı ister | `UnauthorizedAccessException` |
| Y6 | Admin başka Admin'e bürünmek ister | Reddedilir *(kodda mevcut, testi yok)* |

### 12.5 Excel Kütüphanesi Alternatifleri (EPPlus lisans kararı için)

| Kütüphane | Lisans | Uygunluk |
|---|---|---|
| **EPPlus 7.5.2** *(mevcut)* | Ticari / NonCommercial kısıtı | Zengin API (biçim, formül, filtre, yazdırma ayarı) — mevcut `ExportService` bunların hepsini kullanıyor. **Ticari lisans alınırsa hiç kod değişmez** |
| **ClosedXML** | MIT | En yakın alternatif; biçimlendirme ve formül var. `ExportService`'in yeniden yazılması gerekir (~600 satır) |
| **OpenXML SDK** | MIT | En düşük seviye, en bellek verimli; kod hacmi katlanır |
| **NPOI** | Apache-2.0 | Orta seviye, Java portu |
| **ExcelDataReader** | MIT | **Yalnız okuma** — `ImportService` tarafı için EPPlus'ın yerini alabilir, ancak `ExportService` için yetersiz |

**Değerlendirme:** Mevcut export kodunun zenginliği göz önüne alındığında, **ticari EPPlus lisansı büyük olasılıkla en ucuz seçenektir** — ClosedXML'e geçiş ~600 satırlık bir yeniden yazım ve yeni bir regresyon riski demek. Lisans maliyeti bu eforun altındaysa lisans alınmalı. Karar, geliştirici gün maliyeti ile lisans bedelinin karşılaştırılmasıyla verilmeli.

### 12.6 Özet değerlendirme

Dış rapor **kullanışlı ve büyük ölçüde doğru**; özellikle `DataHash`, Testcontainers netleştirmesi, altın senaryolar ve Azure SignalR alternatifi somut katkılar. Buna karşılık **kodu doğrudan okumadan yazıldığı anlaşılıyor**: hâlihazırda uygulanmış dört özelliği (impersonation audit'i, banner, `SqlBulkCopy`, MERGE upsert) eksik sanıyor ve kütüphane sürümünü yanlış veriyor. Sunduğu SQL ve CSP örnekleri kopyalanıp çalıştırılırsa hata üretir.

**Pratik sonuç:** Öneri listesi olarak değerli, **uygulama talimatı olarak değil**. Kabul edilen 14 madde aşağıdaki güncellenmiş yol haritasına işlendi.

---

## 13. Sonuç

PrimSystem, **iş problemini gerçekten anlamış** bir yazılım. Brick paylaşımı, GLN kırılımı, Türkçe karakter normalizasyonu, geçmişe dönük ağırlık versiyonlama, run-rate düzeltmesi gibi detaylar; bir geliştiricinin domain'i ciddiye aldığının kanıtı. Yorumlar bu anlayışı kurumsal hafızaya çevirmiş — bu, projenin en değerli ve en kolay kaybedilebilecek varlığıdır.

Buna karşılık sistemin bugünkü hâli **para hesabına dayanak olan bir uygulamadan beklenen değişmezlik (immutability) ve izlenebilirlik garantilerini vermiyor**. Yeniden hesaplamanın geçmişi silmesi ve sonuçların canlı brick haritasına bağlı olması, teknik borç değil doğrudan iş riskidir: bir prim itirazında "geçen ay şu tutarı görmüştüm" iddiasına karşı sistemin elinde kanıt yok.

Bu nedenle önerilen sıra nettir: **önce emniyet ağı (git + test koşumu + yedek doğrulaması), sonra veri bütünlüğü (versiyonlama + temporal brick), sonra test otomasyonu, ancak ondan sonra mimari güzelleştirme ve UI.** Refactoring cazip ama ikinci sıradadır — çünkü mevcut sistem çalışıyor; asıl risk çalışmamasında değil, **yanlış çalıştığında bunu kanıtlayamamasında**.

---

*Bu rapor yalnızca statik kod incelemesine dayanır. Kod derlenmedi, çalıştırılmadı, testler koşturulmadı ve hiçbir dosya değiştirilmedi. Veritabanı içeriği, üretim yapılandırması ve gerçek çalışma zamanı davranışı incelenmemiştir.*

*§12, dışarıdan sunulan 17 sayfalık "Yönetici Özeti" raporunun kod tabanıyla karşılaştırmalı değerlendirmesidir. Kabul edilen 14 öneri yol haritasına E1–E14 etiketleriyle işlenmiş, kodla çelişen 4 iddia kanıtıyla düzeltilmiştir.*
