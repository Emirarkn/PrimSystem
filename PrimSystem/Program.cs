using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PrimSystem.Data;
using PrimSystem.Services;
using PrimSystem.Components;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Yapılandırılmış loglama — konsol + günlük döngülü dosya (logs/prim-YYYYMMDD.log)
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    // EF Core her DbCommand'i Information'da loglar — her istekte yüzlerce satır + yük.
    // Komut hatalarını (timeout = Error) korurken sorgu dökümünü sustur.
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File("logs/prim-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

// Database — factory pattern (Blazor Server concurrent access safe)
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// DataProtection — anahtarları kalıcı diske yaz. Aksi halde uygulama her restart
// edildiğinde anahtar zinciri yenilenir, ProtectedSessionStorage'daki şifreli oturum
// token'ları çözülemez ve kullanıcılar tekrar giriş yapmak zorunda kalır.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "PrimSystem", "DataProtection-Keys")))
    .SetApplicationName("PrimSystem");

// Services
builder.Services.AddHttpContextAccessor(); // Login'de istemci IP'sini almak için
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<CalculationService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<PhotoService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<SessionState>();

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// DB bağlantı testi — başlangıçta kontrol
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Etkin bağlantı hedefini parolasız logla — "yanlış DB" teşhisini kolaylaştırır
        var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(db.Database.GetConnectionString());
        Log.Information("Veritabanı hedefi: Server={Server} Database={Database}", csb.DataSource, csb.InitialCatalog);
        await db.Database.CanConnectAsync();
        Log.Information("Veritabanı bağlantısı başarılı");

        // Şema self-heal: yeni derleme, DB scripti (db/userbrick-share.sql) çalıştırılmadan
        // başlatılırsa UserBricks.SharePercent eksik kalır ve ona bağlı sorgular çöker
        // (Raporlar "Bir hata oluştu"). İdempotent guard'lı ALTER ile kolonu garanti et.
        try
        {
            // QUOTED_IDENTIFIER ON: filtered index'ler nedeniyle ALTER TABLE bunu gerektirir.
            var added = await db.Database.ExecuteSqlRawAsync(@"
SET QUOTED_IDENTIFIER ON;
IF NOT EXISTS (SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.UserBricks') AND name = 'SharePercent')
BEGIN
    ALTER TABLE dbo.UserBricks
        ADD SharePercent DECIMAL(5,2) NOT NULL CONSTRAINT DF_UserBricks_SharePercent DEFAULT (100);
END;
IF NOT EXISTS (SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.ScorecardWeights') AND name = 'CreatedAt')
BEGIN
    ALTER TABLE dbo.ScorecardWeights
        ADD CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_ScorecardWeights_CreatedAt DEFAULT (SYSUTCDATETIME());
END;
IF NOT EXISTS (SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.ScorecardWeights') AND name = 'UpdatedAt')
BEGIN
    ALTER TABLE dbo.ScorecardWeights ADD UpdatedAt DATETIME2 NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.KPICriteria') AND name = 'UpdatedAt')
BEGIN
    ALTER TABLE dbo.KPICriteria ADD UpdatedAt DATETIME2 NULL;
END");
            Log.Information("Şema self-heal: UserBricks.SharePercent + ScorecardWeights.CreatedAt/UpdatedAt + KPICriteria.UpdatedAt güvencesi tamam.");
        }
        catch (Exception ex)
        {
            // Başlatmayı engelleme; kolon yoksa yine de db/userbrick-share.sql elle çalıştırılabilir.
            Log.Warning(ex, "Şema self-heal (SharePercent/CreatedAt/UpdatedAt) uygulanamadı — db/userbrick-share.sql + db/scorecard-weight-createdat.sql + db/scorecard-kpi-updatedat.sql'i elle çalıştırın.");
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Veritabanı bağlantı hatası");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Güvenlik header'ları — MIME sniffing ve clickjacking'e karşı.
// CSP bilinçli olarak eklenmedi: Blazor Server inline script/style gerektirir.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseSerilogRequestLogging();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

Log.Information("Uygulama başlatıldı — Ortam: {Environment}", app.Environment.EnvironmentName);

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Uygulama beklenmedik şekilde sonlandı");
}
finally
{
    Log.CloseAndFlush();
}