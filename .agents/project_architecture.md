# ANGI — Project Architecture

What the system is and how the pieces fit together. Code-level rules (naming, validation, queries, error mapping) live in `coding_rule.md`; this file does not repeat them.

Source documents (when this file and a document disagree, the document wins and this file gets fixed):

| Document | Covers |
|---|---|
| `ANGI_Data_Dictionary_Ver1.0.xlsx` | Every table and column of schemas `core` and `recommendation` |
| `ANGI_API_Design_Ver1.6.xlsx` | Every backend endpoint, the internal Reco API, error codes, enums, sample JSON |
| `Recommendation_System_Design_Ver1.0.docx` | Recommendation algorithm and the backend ↔ reco split |
| `ANGI_Use_Case_Basic_Descriptions_Ver1.0.docx` | The 66 use cases |

✅ = exists in the repo · 🔲 = planned

---

## 1. Project Overview

**ANGI (Ăn Gì)** — SEP490_G71 capstone. A web app that recommends Vietnamese **regional dishes** to travelers and builds a **feasible day-by-day food roadmap** (location + travel time). This repo is the **backend REST API**; the frontend is a separate Next.js repo and the recommendation service is a separate FastAPI repo.

**Actors:** Guest · Traveler · Restaurant Owner · Moderator · Admin

**Modules** — use these names for feature folders (`DTOs/<Feature>/`, `Validators/<Feature>/`) and commit scopes:

| Iteration | Modules |
|---|---|
| 1 | Auth, Account, Restaurant, Discovery |
| 2 | Roadmap, Social, Notification |
| 3 | Moderation, Administration, Audit |

Recommendation is built in all three iterations inside a **separate service** (see §2 and §7).

**Out of scope:** payment, ordering/delivery, table booking, importing third-party data, native mobile app.

**Performance:** recommendation and roadmap requests must respond within **3 seconds**.

---

## 2. Tech Stack

| Concern | Technology | Status |
|---|---|---|
| Framework | .NET 10, ASP.NET Core Web API (controllers) | ✅ |
| Database | PostgreSQL 16 + `pgvector`, EF Core 10 (`Npgsql.EntityFrameworkCore.PostgreSQL`) | ✅ package |
| DB naming | `EFCore.NamingConventions` (snake_case) | ✅ package |
| Validation | FluentValidation | ✅ package |
| Auth | JWT Bearer + rotating refresh token, BCrypt, Google OAuth | ✅ package · 🔲 config |
| API docs | OpenAPI + Scalar (`/scalar`, Development only) | ✅ |
| Testing | xUnit, Moq, FluentAssertions 7.x | ✅ package |
| Hosting | Render (backend, recommendation service, PostgreSQL), Vercel (frontend) | 🔲 |
| Local dev | Docker Compose: PostgreSQL 16 + pgvector (backend and reco run from the IDE or in the same compose) | ✅ |

### External services 🔲

Each one = an interface in `Application/Common/Interfaces/Services/` + an implementation in `Infrastructure/Services/`.

| Service | Purpose |
|---|---|
| Brevo | Email (verification, reset password) |
| Cloudinary | Image and document upload (`core.media_files`) |
| Map / routing API | Travel time between roadmap stops (provider TBD) |
| AI provider | Roadmap generation (`core.roadmap_generation_jobs`) |
| Recommendation service | Ranking of dishes (see below and §7) |

### Recommendation service

A separate FastAPI service with its **own schema `recommendation`** in the **same PostgreSQL database** (see §6). Endpoints, payloads and error handling: sheet "Reco API (nội bộ)" of the API Design.

- Backend calls it through `IRecommendationService` (typed `HttpClient`) with header `X-Api-Key`. Reco uses snake_case JSON.
- Reco **never reads `core` tables**. Everything it needs comes through its API: dish name/description, survey answers, like/dislike events, candidate dish ids.
- Backend filters candidates first (meal, dish form, price, tags…), reco only scores them and returns **ranked ids + scores + `request_id`**; the backend loads the entities and builds the response.
- Timeout **800 ms** on `/recommendations`. On 5xx or timeout → **fall back to ordering by restaurant rating**, `requestId = null`.
- Two kinds of calls:

| Call | How |
|---|---|
| Survey, `/recommendations`, `/impressions`, survey status | Direct HTTP call from the use case |
| Dish upsert/deactivate, like/dislike feedback | **Transactional outbox** (§7) — never called directly |

---

## 3. Dependency Graph ✅

```
Domain  ◀──  Application  ◀──  Infrastructure
                  ▲                  ▲
                  └──── WebApi ──────┘   (Infrastructure: DI registration only)

Test ──▶ all projects
```

| Project | References | Contains |
|---|---|---|
| Domain | — | Entities, enums, base classes |
| Application | Domain | Use cases, DTOs, validators, **interfaces** (repositories, services) |
| Infrastructure | Application | `ANGIContext`, repositories, external service clients, outbox worker |
| WebApi | Application, Infrastructure | Controllers, middleware, config, `Program.cs` |

Application defines interfaces; Infrastructure implements them. Controllers use only Application interfaces.

---

## 4. Request Flow

```
Client ── HTTPS + JSON (Bearer JWT)
  ▼
ExceptionHandlingMiddleware          catches everything below → ApiResponse (error)
  ▼
CORS → Authentication → Authorization (role, then Mod/Admin permission from DB)
  ▼
Controller                           call use case, wrap DTO in ApiResponse<T>
  ▼
UseCase
  1. validate (FluentValidation)
  2. business logic (+ ICurrentUserService if needed)
  3. repositories  ──▶ ANGIContext ──▶ PostgreSQL (schema core)
  4. IUnitOfWork.SaveChangesAsync()  (writes only, once; outbox rows commit in the same transaction)
  5. map entity → ResponseDto
  ▼
200 / 201 (202 only RM-10, IMP-01)  { "success": true, "message": null, "errorCode": null, "data": { ... } }
```

On error, any layer **throws**; the middleware maps the exception to a status code and returns `{ "success": false, "message": "...", "errorCode": "...", "data": null }` (`coding_rule.md` §5, §7). Endpoints with nothing to return answer 200 with `data: null`; there is no 204. Status codes and the full `errorCode` list: API Design, sheets "Tổng quan" and "Mã lỗi".

Conventions from the API Design that every endpoint follows:

| Topic | Rule |
|---|---|
| Base URL | `/api/v1` |
| JSON | camelCase |
| Auth | Access token 15 min; refresh token 30 days, rotated on every use (`core.user_sessions`). Claims: `sub`, `role`, `email_verified`. |
| Permission | Mod/Admin permission = role default + allow − deny (`core.user_permissions`), read from DB with a short cache, not from the JWT. |
| Paging | `page` (from 1), `pageSize` (default 20, max 100) → `Paged<T>`. Lists that go through reco use `cursor` + `limit` and return `requestId`. |
| Time / money / id | ISO 8601 UTC; business hours `HH:mm` Vietnam time; money = integer VND; ids are `int`/`long` (`uuid` only for `requestId`, `eventUuid`). |
| Delete | Soft delete (`deleted_at`) for users, restaurants, roadmaps, blogs, comments; returns 200 with `data: null`. |
| Rate limit | Login 5 failures / 15 min / email+IP; resend and forgot-password 1 / 60 s; other APIs 100 / min / user → 429 + `Retry-After`. |

---

## 5. Runtime Composition

### Program.cs

```csharp
builder.Services.AddApplication();                            // ✅
builder.Services.AddInfrastructure(builder.Configuration);    // ✅
builder.Services.AddWebApi();                                 // ✅

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();   // 🔲 must be first
app.UseHttpsRedirection();
app.UseCors();                                      // 🔲
app.UseAuthentication();                            // 🔲
app.UseAuthorization();                             // 🔲
app.MapControllers();
```

| Method | Registers |
|---|---|
| `AddApplication()` | Use cases, validators |
| `AddInfrastructure(config)` | `ANGIContext`, repositories, `IUnitOfWork`, external service clients, outbox worker (`BackgroundService`) |
| `AddWebApi()` | Controllers, OpenAPI, JWT, CORS, `ExceptionHandlingMiddleware` |

### Configuration

`appsettings.json` sections (names not final):

```json
{
  "ConnectionStrings": { "Default": "" },
  "Jwt":            { "Issuer": "", "Audience": "", "SecretKey": "", "AccessTokenMinutes": 15, "RefreshTokenDays": 30 },
  "Cors":           { "AllowedOrigins": [ "http://localhost:3000" ] },
  "Google":         { "ClientId": "" },
  "Brevo":          { "ApiKey": "", "SenderEmail": "" },
  "Cloudinary":     { "CloudName": "", "ApiKey": "", "ApiSecret": "" },
  "Recommendation": { "BaseUrl": "", "ApiKey": "", "TimeoutMilliseconds": 800 },
  "Outbox":         { "PollSeconds": 2, "BatchSize": 50, "MaxAttempts": 10 }
}
```

**Never commit secrets.** Local: `dotnet user-secrets` or `appsettings.Local.json` (git-ignored). Render: environment variables, e.g. `ConnectionStrings__Default`, `Jwt__SecretKey`.

---

## 6. Database

### Setup ✅

```csharp
services.AddDbContext<ANGIContext>(options =>
    options.UseNpgsql(configuration.GetConnectionString("Default"),
                      npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "core"))
           .UseSnakeCaseNamingConvention());

// in ANGIContext.OnModelCreating
modelBuilder.HasDefaultSchema("core");
```

Local connection string: `Host=localhost;Port=5432;Database=angi;Username=<backend role>;Password=<secret>`

### One database, two schemas

| Schema | Owner | Contents | Migrations |
|---|---|---|---|
| `core` | Backend | Users, restaurants, dishes, menus, roadmaps, blogs, reports, notifications, audit logs, outbox (38 tables) | EF Core |
| `recommendation` | Recommendation service | Dish/user vectors (`pgvector`), scores, stats, logs (9 tables) | The service's own migrations |

| Access | Allowed |
|---|---|
| Backend reads/writes `core` | ✅ (backend's own DB role and connection string) |
| Service reads/writes `recommendation` | ✅ (reco's own DB role and connection string) |
| Service reads `core` | ❌ — data comes through the Reco API / outbox |
| Backend reads or writes `recommendation` | ❌ — ask the service over HTTP |
| FK between the two schemas | ❌ — `user_id` / `dish_id` in `recommendation` are soft references |

Rules:

- **EF Core does not map `recommendation.*`**, otherwise migrations would try to create or drop those tables.
- `core.users.id` and `core.dishes.id` are the `user_id` / `dish_id` the service stores. Never reuse or renumber them.
- `pgvector` is enabled by the service's migrations: `CREATE EXTENSION IF NOT EXISTS vector;`

### Conventions

| Topic | Rule |
|---|---|
| Naming | snake_case, generated automatically: `RestaurantImage` → `restaurant_images`, `PasswordHash` → `password_hash`. Never rename by hand. |
| Primary key | As in the Data Dictionary: `integer` for `users`, `dishes`; `bigint` for most other tables; `smallint` for lookup tables (`roles`, `permissions`, `tags`). Identity columns. |
| Date/time | Always **UTC** (`DateTime.UtcNow`) → `timestamptz`. Npgsql throws on local time. |
| Location | `latitude`, `longitude` as `numeric(9,6)`. "Nearby" = bounding-box filter, then Haversine. Travel time comes from the map API. No PostGIS. |
| Status / enum | `varchar` + `CHECK` with lowercase snake_case values (e.g. `pending_verification`). Domain enums are mapped to exactly those strings with a value converter; valid values: API Design, sheet "Enum". |
| Email | `citext` (case-insensitive unique). |

### Base classes ✅

```csharp
public abstract class BaseEntity<TKey> { public TKey Id { get; set; } = default!; }
public interface IHasCreatedAt { DateTime CreatedAt { get; set; } }
public interface IHasUpdatedAt { DateTime UpdatedAt { get; set; } }
public interface ISoftDelete    { DateTime? DeletedAt { get; set; } }
```

- Columns follow the Data Dictionary exactly: an entity implements only the interfaces for the timestamp columns its table has (e.g. `audit_logs` is append-only, no `updated_at`). Tables with a composite key (`role_permissions`, `dish_tags`, `blog_likes`, ...) and `review_replies` (key = `review_id`) do not inherit `BaseEntity`.
- `CreatedAt` / `UpdatedAt` are set automatically in `ANGIContext.SaveChangesAsync`. Use cases never set them.
- **Soft delete** only for tables with `deleted_at` (users, restaurants, roadmaps, blogs, blog comments): the use case sets `DeletedAt = DateTime.UtcNow`; never call `Remove()` on these entities. A global filter (`DeletedAt == null`) hides deleted rows; use `IgnoreQueryFilters()` when deleted rows are needed. A required navigation to a soft-deleted row (e.g. `Blog.Author`) loads as `null`.
- Required FKs are `RESTRICT` unless the configuration sets `Cascade` (owned child rows: images, business hours, dish tags, menu items, roadmap days/items, ...).
- A column with a constant database default (`dish_form = 'other'`, `is_available = true`) also gets that default as the C# property initializer; EF always sends the value.
- Unique indexes must skip deleted rows: `.IsUnique().HasFilter("deleted_at IS NULL")`.
- Audit logs (Admin "View System Audits Log", Mod "View User Audit Log") use `core.audit_logs`.

### Migrations

```bash
dotnet tool install --global dotnet-ef   # once

dotnet ef migrations add <Name> -p ANGI.Infrastructure -s ANGI.WebApi -o Persistences/Migrations
dotnet ef database update -p ANGI.Infrastructure -s ANGI.WebApi
```

In Development the API applies pending migrations at startup (`app.Services.MigrateDatabaseAsync()` in `Program.cs`, implemented in `Infrastructure/Persistences/DatabaseMigrationExtensions.cs`); it refuses to start when the model has changes without a migration. Staging/production never migrate on startup: migrations run as a separate deploy step.

One migration per feature change, PascalCase name (e.g. `AddRestaurant`). Never edit generated files. `CHECK` constraints for enum columns are generated from the enums (`ApplyEnumConventions`); other `CHECK`s use `HasCheckConstraint`; `citext` uses `HasPostgresExtension`. Only what EF cannot express (outbox triggers, sequence fixes after seeding) goes into the migration with `migrationBuilder.Sql(...)`.

Lookup data seeded in `InitialCreate` with `HasData`: `roles` (1 TRAVELER, 2 RESTAURANT_OWNER, 3 MOD, 4 ADMIN), `permissions` (13 codes from sheet "Enum"), `role_permissions` (MOD: first 10, ADMIN: all 13).

---

## 7. Backend → Recommendation sync (transactional outbox) 🔲

Dish changes and like/dislike must reach reco even if reco is down, and must never be applied twice.

Outbox rows are written by database triggers created in `InitialCreate` ✅ (`trg_dishes_outbox`, `trg_dish_feedbacks_outbox`), so use cases only write `dishes` / `dish_feedbacks`. Changing `is_available` or `price` writes no event. The worker is 🔲.

```
UseCase writes core (dishes / dish_feedbacks)
   └─ same transaction ─▶ core.outbox_messages (status = pending)
                                   │
OutboxWorker (BackgroundService) ──┘ polls, locks a batch (locked_until)
   ├─ calls Reco with header Idempotency-Key = event_uuid
   ├─ 2xx               → status = done, processed_at
   ├─ 5xx / timeout     → attempt_count++, next_attempt_at = backoff
   └─ 4xx or too many attempts → status = dead (Admin can see it)
```

| `event_type` | Written when | Reco endpoint |
|---|---|---|
| `DISH_UPSERTED` | Dish created, name/description/form changed, dish shown again (incl. menu approval) | `POST /dishes/upsert` |
| `DISH_DEACTIVATED` | Dish removed by menu approval, hidden by Mod | `DELETE /dishes/{dish_id}` |
| `FEEDBACK_CREATED` | Like/dislike on a roadmap item | `POST /feedback` |

- Messages with the same `aggregate_key` (`dish:<id>`, `user:<id>`) are sent **in order**; different keys may run in parallel.
- Reco returns 503 + `Retry-After` when the dish vector is not ready yet; the worker treats it as retryable.
- The full column list is in the Data Dictionary (`core.outbox_messages`); the payload of each event matches the Reco API request body.
