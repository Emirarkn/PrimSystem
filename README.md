# PrimSystem

A **quarterly bonus calculation, performance scoring and field analytics** platform for field sales teams (ST / Sales Representatives). Built around a pharmaceutical/OTC distribution model, it compares target vs. actual at the brick (territory segment) level, produces a weighted score from 4 KPIs, and converts that score into a bonus amount (TRY) via a tier table.

- **Platform:** .NET 8 · Blazor Server (Interactive Server render mode)
- **Database:** SQL Server (EF Core 8.0.13, DbContextFactory pattern)
- **Code size:** ~22,400 lines (app ~21,100 + tests ~1,270), 47 Razor components, 12 services, 40 entities
- **Language:** UI and code comments are in Turkish

---

## 1. What does it do?

| Stage | Details |
|---|---|
| **Data input** | 8 different Excel templates (Sell-Out, Orders, Targets, Route, Visit Plan, Pharmacy Plan, Product-Brand master, Brick assignment) are imported with a dry-run preview + persistent upload |
| **Normalization** | Canonical keys that solve the Turkish İ/ı casing problem, Levenshtein-based match suggestions, persistent alias (NameAlias) memory |
| **Scoring** | Product-weighted sales score + Route compliance + Distribution + Strike Rate → weighted final score |
| **Bonus** | Final score ≥ 85% gate → tier multiplier × role-based base amount = bonus (TRY) |
| **Publishing** | Admin calculates → reviews → publishes; ST/BM users only see results after publishing (hold/resume supported) |
| **Analytics** | Reports broken down by region / brick / pharmacy (GLN) / product, order leaderboard, star badges, Excel export |
| **Side modules** | Task assignment (BM→ST), field photo approval workflow, notification center, session tracking, audit log |

---

## 2. Scoring model

```
Product score       = min(actual / target, 1.05) × product_weight / 100
Sales score         = Σ product scores
Route score         = RutaUyum.Rate (imported rate)
Distribution score  = min(bricks with sales / assigned bricks, 1.00)
Strike score        = min(planned GLNs with orders / planned GLNs, 1.00)   [null if no plan]

Final score = 0.60·Sales + 0.15·Route + 0.15·Distribution + 0.10·Strike   (weights from KPICriteria)
Gate        = Final score ≥ 0.85
Bonus (TRY) = eligible && gate ? RolePrimBase × PrimTier.Multiplier : 0
```

Additional rules:

- **105% cap** — over-achieving the target is not rewarded beyond the cap; returns (negative) do not push the score below zero (floored at 0).
- **Tenure threshold** — if `MonthsWorked(hireDate, quarterStart) < User.MinMonths` (default 3 months), `IsEligible = false` and the bonus is 0.
- **Run-rate mode** (optional, `SystemSettings.RunRateMode`) — prorates the target by the number of periods with sell-out data; fixes the unfairness of comparing a 3-month target against 2 months of sales.
- **Shared bricks** — target/sell-out/order attribution is split via `UserBricks.SharePercent` (single owner = 100).

> **Note:** The `ScorecardWeight.MinRate` and `KpiCriteria.MinRate` fields are saved and displayed on screen but are **not used** in the score calculation. Also, if product weights do not sum to 100, only a visual warning is shown and saving is not blocked; there is no sum check at all for KPI weights.

### 2.1 Sales Representative (ST) Experience

An ST logs in with their own account and sees **only their own data** (`specificUserId` filter + `publishedOnly: true`). An unpublished calculation never reaches the ST.

The home page shows a single personal card:

- **ScoreGauge** — final score ring with an 85% gate notch, on a 0–105 scale
- **KpiBreakdownBars** — separate bars for the 4 KPIs, each with its weight label; a faded "—" when there is no data
- **Bonus card** — the earned amount (₺) or the reason ("Threshold not met" / "Non-bonus period" / "No tier defined")
- **Product detail** — target / actual / % rate per product, with a ↑ marker when the cap was applied
- **Radar chart** — 4 KPIs + a dashed 85% threshold circle (Chart.js)

On the `/reports` and `/eczaneler` pages, the "My Brick Performance" and "Top Pharmacies in My Bricks" panels are narrowed to the ST's own scope. STs can also update task status, upload field photos and follow their notifications.

### 2.2 Web-Based Operation

The application runs on the server and is accessed from the browser (Blazor Server + SignalR). C# code is never sent to the client; the bonus formula and queries stay on the server. A circuit is kept on the server for each user.

Deployment requirements:

| Requirement | Note |
|---|---|
| Windows Server + IIS (ASP.NET Core Hosting Bundle) | or Linux + Kestrel/nginx |
| WebSocket-enabled reverse proxy | required for the SignalR circuit |
| Sticky sessions | `SessionStore` and `LoginThrottle` are in-memory — mandatory in multi-server setups |
| HTTPS certificate | HSTS + HTTPS redirection active in production |
| Environment variables | `ConnectionStrings__DefaultConnection`, `Smtp__Password`, `AppBaseUrl`, `AllowedHosts` |
| SQL Server access | the app account currently also needs **DDL permissions** (startup self-heal block) |
| Writable disk | `logs/` (30 days) + DataProtection key folder |
| Internet access (client) | **Chart.js is loaded from a CDN** — on a closed network, charts silently stay empty |

---

## 3. Roles

| Role | RoleId | Can see |
|---|---|---|
| **Admin** | 1 | Everything: import, calculation, publishing, scorecard, bonus settings, brick management, control panel, impersonation |
| **Yonetici** (Channel Manager) | 2 | All regions — Overview drill-down, organization, raw calculation export |
| **BM** (Regional Manager) | 3 | Own region — Region Analysis, task assignment, photo review |
| **ST** (Sales Representative) | 4 | Own results + pharmacies in own bricks, tasks, photo upload |

Authorization works in two layers:

1. **UI guards** — Razor pages use `SessionState` (`IsAdmin`, `CanManage`, `CanViewAll`) to show `LoginRequired` / `NotAuthorizedView`.
2. **Service layer** — fresh role verification from the DB on every call: `EnsureAdminAsync` in 16 `ImportService`, 14 `CalculationService` and 2 `AuthService` methods; `EnsureCanExportAllAsync` in `ExportService` (Admin/Yonetici); `CanReviewAsync` in `PhotoService` (Admin or the relevant region's BM); BM–ST relationship check in `TaskService`; `SessionState` forbids impersonating an admin.

> **Note:** These checks cover **write** operations. Read scope (which role sees which records) is not defined in the service signature, but by the filter parameter passed by the calling page.

Authentication does not use ASP.NET Core Identity; it builds its own lightweight layer with BCrypt + `SessionStore` + `ProtectedSessionStorage`. The `UserSessions` table stores login/logout/IP/last-seen information.

---

## 4. Setup

### Requirements

- .NET 8 SDK
- SQL Server (LocalDB / Express / full edition)
- `dotnet-ef` 10.0.8 (local tool — `dotnet tool restore`)

### Steps

```bash
# 1) Dependencies
dotnet tool restore
dotnet restore

# 2) Connection string (development)
cd PrimSystem
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=PrimDB;Trusted_Connection=True;TrustServerCertificate=True;"
dotnet user-secrets set "Smtp:Password" "SMTP_PASSWORD"

# 3) Schema
# EMPTY database → apply migrations:
dotnet ef database update
# EXISTING database → follow the baseline method in db/MIGRATIONS.md (do NOT run database update)

# 4) Indexes and extra schema scripts (idempotent)
sqlcmd -S localhost -d PrimDB -i db/indexes.sql

# 5) Run
dotnet run
```

In production, secrets are provided via environment variables (`Smtp__Password`, `ConnectionStrings__DefaultConnection`, `AppBaseUrl`, `AllowedHosts`) — details: [`PrimSystem/SECURITY-SETUP.md`](PrimSystem/SECURITY-SETUP.md).

### Tests

```bash
dotnet test PrimSystem.Tests/PrimSystem.Tests.csproj
```

> **Note:** `PrimSystem.slnx` contains only the main project; the test project is not added to the solution, so its path must be given explicitly.

---

## 5. Folder structure

```
PrimSystem.slnx
PrimSystem/
  Program.cs                  # DI, Serilog, DataProtection, schema self-heal, security headers
  Data/
    Entities.cs               # 40 entities
    AppDbContext.cs           # DbSets, precision, indexes, cascade rules, soft-delete filter
    RoleConstants.cs
  Services/
    ScoreCalculator.cs        # PURE score math (testable core)
    CalculationService.cs     # calculation + publishing + all analytics queries (2,107 lines)
    ImportService.cs          # 8 Excel imports + matching + rollback (2,492 lines)
    ExportService.cs          # formatted Excel outputs with EPPlus
    AuthService.cs / SessionState.cs / SessionStore.cs / LoginThrottle.cs
    TaskService.cs / PhotoService.cs / NotificationService.cs / AuditService.cs / EmailService.cs
  Components/
    Pages/                    # 23 pages (route-based)
    Shared/                   # 18 reusable components
    Layout/
  Migrations/                 # 11 EF migrations (InitialBaseline + incrementals)
  db/                         # 24 manually run SQL scripts + MIGRATIONS.md
  wwwroot/                    # app.css, fileUpload.js
PrimSystem.Tests/             # 111 xUnit tests (score, import validation, export, auth, task)
```

---

## 6. Routes

| Route | Page | Access |
|---|---|---|
| `/` | Home / Dashboard | All roles (content depends on role) |
| `/reports` | Reports | All roles (scope narrowed by role) |
| `/eczaneler` | Pharmacy Analysis | All roles (scope narrowed) |
| `/notifications` | Notifications | All roles |
| `/bolgem` | Region Analysis | BM |
| `/gorevler` | Tasks | BM, ST |
| `/overview` | Türkiye Overview | Yonetici, Admin |
| `/organization` | Organization | Yonetici, Admin |
| `/photos` | Photo Upload | ST |
| `/photo-review` | Photo Review | Admin, BM |
| `/calculate` · `/import` · `/periods` · `/scorecard` · `/prim-config` · `/bricks` · `/panel` · `/activity` | Administration | Admin |
| `/login` · `/forgot-password` · `/reset-password` · `/change-password` | Authentication | — |

---

## 7. Security notes

- Passwords are hashed with **BCrypt**; `MustChangePassword` is enforced on first login.
- Password reset tokens are stored in the DB as a **SHA256 hash**, valid for 1 hour.
- Brute force: per username, 5 failed attempts within 15 minutes → 15-minute lockout (`LoginThrottle`, in-memory).
- Session: token in `ProtectedSessionStorage` + in-memory `SessionStore` (8-hour idle timeout); DataProtection keys are persisted to disk.
- All raw SQL is **parameterized**; dynamic parts (metric, plan predicate) come from whitelisted `switch` expressions.
- Security headers: `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`. CSP is intentionally not added (Blazor Server inline script requirement).
- Auditing: **28 different actions** are written to `AuditLogs` (password operations, import/rollback, publishing, ranking, configuration, tasks, photos, impersonation start/end).

> **Audit gaps:** `Calculate` (running a calculation), `Login`/`Logout`/`LoginFailed` and user CRUD actions are not audited. The `IpAddress` field exists but is not populated. There is no UI page for viewing audit records.

### Test status

**111 xUnit tests** (1,272 lines): score math 41 · import validation 46 · export 9 · security 8 · tasks 7.

Not tested: `CalculateQuarterAsync` orchestration, the import flows themselves, DB integration, UI components, **authorization scope** (the "an ST cannot see someone else's bonus" rule is not tested), load/performance. No CI; coverlet is included but coverage is not reported.

---

## 8. Known limitations

- **EPPlus `LicenseContext.NonCommercial`** — a license is required for commercial use.
- **In-memory session/throttle** — assumes a single server; horizontal scaling requires sticky sessions or a distributed cache.
- **Recalculation overwrites rather than keeping history** — when a quarter is recalculated, that quarter's `Calculations` rows are deleted and recreated, and the publish status is reset. Previous versions of a published bonus amount are not kept.
- **Live brick map** — target/actual attribution is based on the current state of `UserBricks`; when assignments change, past quarters' results can change too. `CalcFreshnessBanner` warns about this, but there is no automatic freezing.
- **Schema duality** — EF migrations and `db/*.sql` scripts run in parallel; `Program.cs` contains a "self-heal" block that adds missing columns.

Detailed evaluation, architecture analysis and improvement roadmap (in Turkish): [`PROJE-RAPORU.md`](PROJE-RAPORU.md)
