# Sır (Secret) Yönetimi

Hassas değerler (SMTP parolası, prod connection string) **artık `appsettings.json` içinde tutulmaz**.
Geliştirmede **.NET User Secrets**, üretimde **environment variable** kullanılır. ASP.NET Core
yapılandırma sağlayıcıları bu kaynakları otomatik olarak `appsettings.json`'ın üzerine yazar.

## Geliştirme (User Secrets)

Proje `UserSecretsId` ile zaten yapılandırıldı. Geliştiricinin makinesinde tek seferlik:

```bash
cd PrimSystem
dotnet user-secrets set "Smtp:Password" "GERÇEK_SMTP_PAROLASI"
# Gerekirse yerel connection string override:
# dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=...;User Id=...;Password=...;"
```

Bu değerler `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json` altında, **repo dışında** saklanır.
Liste/temizleme: `dotnet user-secrets list` / `dotnet user-secrets clear`.

## Üretim (Environment Variable)

İç içe anahtarlar için ayraç olarak `__` (çift alt çizgi) kullanılır:

```
Smtp__Password=GERÇEK_SMTP_PAROLASI
ConnectionStrings__DefaultConnection=Server=...;User Id=...;Password=...;TrustServerCertificate=True;
AppBaseUrl=https://gercek-domain
AllowedHosts=gercek-domain
```

IIS / Windows Service / Docker / systemd hangisi kullanılıyorsa env var oradan tanımlanır.
Kod değişikliği gerekmez — `builder.Configuration` bu değerleri otomatik okur.

## Notlar
- `appsettings.json` ve `appsettings.Production.json` içine **gerçek parola/sır yazmayın**.
- `appsettings.Production.json` içindeki `DOMAIN_BURAYA_YAZILACAK` yer tutucularını gerçek domain ile değiştirin
  (env var ile de override edilebilir).
- SMTP: Office365 basic auth kapalıysa hesaba **SMTP AUTH / uygulama parolası** tanımlanmalı ya da
  OAuth2/Microsoft Graph'a geçilmelidir (EmailService MailKit tabanlıdır, modern TLS destekler).
