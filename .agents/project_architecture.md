# ANGI — Project Architecture

How the backend is built and how it talks to the rest of the system. Code-level rules (naming, validation, queries, error mapping, DI) are in `coding_rule.md`; branches and commits in `commit_guide.md`.

Source documents win over this file; when they disagree, fix this file.

| Document | Covers |
|---|---|
| `.docs/ANGI_Data_Dictionary_Ver1.2.xlsx` | Every table and column of schemas `core` and `recommendation` |
| `.docs/ANGI_API_Design_Ver1.8.xlsx` | Backend endpoints, internal Reco API, error codes, enums, sample JSON |
| `.docs/ANGI_Jira_Plan_Ver1.2.xlsx` | Tasks, owners, dates |
| `angi-reco` repo: `.docs/Recommendation_System_Design_Ver1.1.docx` | Recommendation algorithm, backend ↔ reco split |
| `ANGI_Use_Case_Basic_Descriptions_Ver1.0.docx` (team drive) | The 66 use cases |

---

## 1. Overview

**ANGI (Ăn Gì)**, SEP490_G71: recommends Vietnamese regional dishes to travelers and builds a day-by-day food roadmap. This repo is the **backend REST API**. The frontend (Next.js) and the recommendation service (FastAPI) are separate repos.

- **Actors:** Guest, Traveler, Restaurant Owner, Mod, Admin
- **Out of scope:** payment, ordering/delivery, table booking, third-party data import, native mobile app
- **Performance:** recommendation and roadmap requests answer within 3 s

**Modules**: these names are the `<Module>` folders in Application (`coding_rule.md` §1) and, lowercase, the commit scopes.

| Iteration | Modules |
|---|---|
| 1 | Auth, Account, Restaurant, Discovery |
| 2 | Roadmap, Social, Notification |
| 3 | Moderation, Administration, Audit |

Recommendation runs in its own service across all three iterations (§2, §7).

---

## 2. Tech Stack & External Services

| Concern | Technology |
|---|---|
| Framework | .NET 10, ASP.NET Core Web API (controllers) |
| Database | PostgreSQL 16 + pgvector; EF Core 10 (`Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`) |
| Validation | FluentValidation |
| Auth | JWT Bearer, rotating refresh token, BCrypt, Google sign-in |
| API docs | OpenAPI + Scalar (`/scalar`, Development only) |
| Tests | xUnit, Moq, FluentAssertions 7.x (8.x is paid for commercial use) |
| Hosting | Render (backend, reco, PostgreSQL), Vercel (frontend) |

### External services

Each service = an interface in `Application/Common/Interfaces/Services/` + an implementation in `Infrastructure/Services/`.

| Service | Interface | Purpose |
|---|---|---|
| Cloudinary | `ICloudinaryService` | Image and document upload (`core.media_files`); private files get signed URLs valid 5 min |
| Brevo | planned | Email: verification, password reset |
| Google | planned | Verify Google ID tokens (`Google.Apis.Auth`) |
| Recommendation service | `IRecommendationService` (planned) | Dish ranking |
| Mapbox Directions | `IRouteService` (planned) | Distance and time of each leg of a roadmap day (RM-14) |
| AI provider | planned | Roadmap generation (`core.roadmap_generation_jobs`) |

### Recommendation service

- Typed `HttpClient`, header `X-Api-Key`, snake_case JSON. Endpoints: sheet "Reco API (nội bộ)".
- Responses are not wrapped in `ApiResponse`: a 2xx body is the DTO itself, an error body is `{ "error_code": "...", "message": "...", "errors": { field: [messages] } }` (`errors` only for 422 `VALIDATION_FAILED`). Branch on `error_code`, never on `message`.
- The backend filters candidates (meal, form, price, tags, distance) and sends them sorted by restaurant rating, then dish `like_count` (at most 2000); reco only scores them (stable sort, so ties keep that order) and returns ranked ids + scores + `request_id`; the backend loads entities and builds the response.
- `/recommendations` timeout 800 ms. On 5xx or timeout: order by restaurant rating, `requestId = null`.
- Survey, `/recommendations`, `/impressions`, survey status: direct call from the use case. Dish changes and like/dislike: outbox only (§7).

### Mapbox (RM-14)

- One Directions call per day (`/directions/v5/mapbox/{profile}/{lng,lat;...}`), up to 25 points; read `routes[0].legs[].distance` (m) and `.duration` (s). Profiles `driving` (default), `walking`, `cycling`.
- Points: origin (if any) → the day's items by meal slot, then `sortOrder`. Origin = GPS sent by the app, else the pinned point, else none. Skip the first leg when the origin is more than 30 km (straight line) from the first item.
- `IMemoryCache` 24 h, key = profile + coordinates (origin rounded to 3 decimals ≈ 100 m). Timeout 3 s; on failure estimate straight line × 1.3 at 25 / 12 / 4 km/h and return `isEstimated = true`.
- The secret token stays in backend config; the frontend has its own public `pk.*` token for maps. "Open in Google Maps" is a link built by the frontend.

**Roadmap origin** (`roadmaps.origin_mode`): `pinned` stores `origin_latitude/longitude/label`; `gps` stores nothing and the app sends coordinates on each RM-10 / RM-14 call; `none` = province only. Coordinates exist if and only if `pinned` (CHECK). Switching away from `pinned` clears the coordinates in the use case (RM-04).

---

## 3. Solution Structure

```
Domain  ◀──  Application  ◀──  Infrastructure
                  ▲                  ▲
                  └──── WebApi ──────┘   (Infrastructure: DI registration only)
Test ──▶ all projects
```

| Project | Contains |
|---|---|
| Domain | Entities, enums, `BaseEntity` and timestamp / soft-delete interfaces. No dependencies. |
| Application | Use cases, DTOs, validators, exceptions, and the **interfaces** for repositories and services |
| Infrastructure | `ANGIContext`, configurations, migrations, repositories, external service clients |
| WebApi | `Program.cs`, controllers, middlewares, `Configs/` (JWT, CORS, rate limit), `ApiResponse<T>` |

Application declares interfaces, Infrastructure implements them, controllers use only Application interfaces.

---

## 4. Request Pipeline

Order in `Program.cs`:

```
ExceptionHandlingMiddleware      every exception → ApiResponse error (coding_rule.md §7)
[Development] migrate DB, OpenAPI + Scalar (/scalar)
HTTPS redirection
CORS (policy "Frontend")
RateLimitPartitionMiddleware     reads the email of login / resend / forgot-password for the rate-limit key
LoginFailureLimitMiddleware      after 5 INVALID_CREDENTIALS per email + IP in 15 min → 429 until the window ends; success clears the count
Authentication (JWT)             401 UNAUTHORIZED as ApiResponse
Rate limiter                     resend / forgot-password and general limits → 429 TOO_MANY_REQUESTS + Retry-After
Authorization                    403 FORBIDDEN as ApiResponse
Controllers → use case → repositories → ANGIContext → PostgreSQL (core)
```

A use case: validate (FluentValidation) → business logic → repositories → `IUnitOfWork.SaveChangesAsync()` once for writes → map to DTO. Response shape and status codes: `coding_rule.md` §5.

Conventions every endpoint follows (from the API Design):

| Topic | Rule |
|---|---|
| Base URL, JSON | `/api/v1`, camelCase |
| Tokens | Access 15 min; refresh 30 days, rotated on every use (`core.user_sessions`). Claims: `sub`, `role`, `email_verified`. |
| Mod/Admin permission | Role default + allow − deny (`core.user_permissions`), read from DB with a short cache, not from the JWT |
| Paging | `page` (from 1), `pageSize` (default 20, max 100) → `Paged<T>`. Lists ranked by reco use `cursor` + `limit` and return `requestId`. |
| Time, money, ids | ISO 8601 UTC; business hours `HH:mm` Vietnam time; integer VND; `int`/`long` ids (`uuid` only for `requestId`, `eventUuid`) |
| Delete | Soft delete for users, restaurants, roadmaps, blogs, comments; 200 with `data: null` |
| Rate limit | Login 5 failures / 15 min per email + IP; resend and forgot-password 1 / 60 s; other APIs 100 / min per user (per IP when anonymous) |

---

## 5. Configuration

| Method | Registers |
|---|---|
| `AddApplication()` | Validators (`AddValidatorsFromAssembly`); use cases as they are added |
| `AddInfrastructure(config)` | `ANGIContext`, `IUnitOfWork`, `ICloudinaryService`; later repositories, other service clients, `IMemoryCache`, outbox worker |
| `AddWebApi(config)` | Controllers + `VALIDATION_FAILED` response for model binding, OpenAPI, JWT, CORS, rate limit, middlewares |

`appsettings.json` holds every section with non-secret defaults:

| Section | Keys |
|---|---|
| `ConnectionStrings` | `Default` |
| `Jwt` | `Issuer`, `Audience`, `SecretKey` (≥ 32 bytes, checked at startup), `AccessTokenMinutes`, `RefreshTokenDays` |
| `Cors` | `AllowedOrigins` |
| `RateLimit` | `GeneralPermitLimit`, `GeneralWindowSeconds`, `LoginPermitLimit`, `LoginWindowMinutes`, `SensitiveAuthPermitLimit`, `SensitiveAuthWindowSeconds` |
| `Cloudinary` | `CloudName`, `ApiKey`, `ApiSecret` |
| Planned | `Google:ClientId`; `Brevo:ApiKey, SenderEmail`; `Recommendation:BaseUrl, ApiKey, TimeoutMilliseconds (800)`; `Mapbox:BaseUrl, AccessToken, TimeoutMilliseconds (3000), CacheHours (24)`; `Outbox:PollSeconds (2), BatchSize (50), MaxAttempts (10)` |

**Secrets are never committed.**

- Local: `dotnet user-secrets set "<Section>:<Key>" "<value>" --project ANGI.WebApi`. Required to start: `ConnectionStrings:Default`, `Jwt:SecretKey`.
- Render: environment variables with `__`, e.g. `ConnectionStrings__Default`, `Jwt__SecretKey`, `Mapbox__AccessToken`.

---

## 6. Database

Local setup (Docker, roles, connection string): `README.md`.

### Schemas

| Schema | Owner and role | Tables | Migrations |
|---|---|---|---|
| `core` | Backend, `angi_backend` | 38 | EF Core (this repo) |
| `recommendation` | Reco service, `angi_reco` | 9 | Reco repo (Alembic) |
| `public` | nobody | none | holds the `citext` and `vector` extensions |

- Each role reads and writes only its own schema (enforced by database permissions). The backend asks reco over HTTP; reco gets data through its API and the outbox.
- EF Core does not map `recommendation.*`. No FK between the schemas: `user_id` / `dish_id` in `recommendation` are `core.users.id` / `core.dishes.id`; never reuse or renumber them.

### Conventions

| Topic | Rule |
|---|---|
| Columns | Exactly as the Data Dictionary. snake_case names are generated; never rename by hand. |
| Keys | Identity columns: `integer` for `users`, `dishes`; `smallint` for `roles`, `permissions`, `tags`; `bigint` for the rest |
| Time | UTC only (`DateTime.UtcNow`) → `timestamptz` |
| Enums | `varchar` + `CHECK`, values snake_case as in sheet "Enum" (`pending_verification`). The converter and the CHECK are generated from the C# enum (`ApplyEnumConventions`). `OutboxEventType` is stored UPPER_CASE, `FeedbackValue` as smallint 1 / -1. |
| Email | `citext` |
| Location | `numeric(9,6)`, required on `restaurants`. "Nearby" = bounding box, then Haversine. No PostGIS; travel time comes from Mapbox and is never stored. |
| JSON | `jsonb` columns are `string` properties |

### Entities

```csharp
public abstract class BaseEntity<TKey> { public TKey Id { get; set; } = default!; }
public interface IHasCreatedAt { DateTime CreatedAt { get; set; } }
public interface IHasUpdatedAt { DateTime UpdatedAt { get; set; } }
public interface ISoftDelete    { DateTime? DeletedAt { get; set; } }
```

- An entity implements only the interfaces for the columns its table has. Composite-key tables and `review_replies` (key = `review_id`) do not inherit `BaseEntity`.
- `CreatedAt` / `UpdatedAt` are set in `ANGIContext.SaveChangesAsync`; use cases never set them.
- **Soft delete** (users, restaurants, roadmaps, blogs, blog_comments): set `DeletedAt = DateTime.UtcNow`, never call `Remove()`. A global filter hides deleted rows (`IgnoreQueryFilters()` to see them); a required navigation to a deleted row loads as `null`. Unique indexes skip deleted rows (`HasFilter("deleted_at IS NULL")`).
- FKs are `RESTRICT` unless the configuration sets `Cascade` (owned child rows: images, business hours, dish tags, menu items, roadmap days/items, sessions/tokens). `roadmap_item_id` in `dish_feedbacks` and `restaurant_reviews` is `SET NULL`.
- A constant DB default (`dish_form = 'other'`, `is_available = true`) is repeated as the C# property initializer; EF always sends the value.
- A non-nullable `decimal` (e.g. restaurant coordinates) defaults to 0 in C#: validators must require it, NOT NULL alone does not catch it.

### Migrations

```bash
dotnet ef migrations add <Name> -p ANGI.Infrastructure -s ANGI.WebApi -o Persistences/Migrations
dotnet ef database update -p ANGI.Infrastructure -s ANGI.WebApi
```

- Development applies pending migrations at startup (`MigrateDatabaseAsync`) and refuses to start when the model has changes without a migration. Other environments migrate as a deploy step, never at startup.
- One migration per PR, PascalCase name (`AddRestaurant`). Do not edit generated code, except removing an EF-generated default that hides missing data (comment why).
- CHECKs use `HasCheckConstraint`, extensions `HasPostgresExtension`. Only what EF cannot express goes into `migrationBuilder.Sql(...)`: outbox triggers, sequence fixes after seeding.
- Seeded in `InitialCreate`: `roles` (1 TRAVELER, 2 RESTAURANT_OWNER, 3 MOD, 4 ADMIN), 13 `permissions` (sheet "Enum"), `role_permissions` (MOD: 1–10, ADMIN: all).

| Migration | Content |
|---|---|
| `InitialCreate` | 38 tables, CHECKs, `citext`, outbox triggers, lookup seed |
| `AddRoadmapOriginAndRequireRestaurantCoordinates` | Data Dictionary 1.1: `roadmaps.origin_*`, restaurant coordinates NOT NULL |

---

## 7. Outbox (backend → reco)

Dish changes and like/dislike must reach reco even when it is down, and never twice.

Database triggers from `InitialCreate` write `core.outbox_messages` in the same transaction, so use cases only write `dishes` / `dish_feedbacks`:

| Change | `event_type` | Reco endpoint |
|---|---|---|
| Dish inserted active; name / description / form changed; shown again | `DISH_UPSERTED` | `POST /dishes/upsert` |
| Dish goes from active to hidden / removed | `DISH_DEACTIVATED` | `DELETE /dishes/{dish_id}` |
| Like / dislike inserted (same `event_uuid` as `dish_feedbacks`) | `FEEDBACK_CREATED` | `POST /feedback` |

`is_available` and `price` changes write nothing. Payloads match the Reco API request bodies.

The worker (`BackgroundService`, planned):

```
poll pending rows → lock a batch (locked_until) → call reco with Idempotency-Key = event_uuid
  2xx                       → done, processed_at
  5xx / timeout / 503       → attempt_count++, next_attempt_at = backoff
  4xx or too many attempts  → dead (visible to Admin, OPS-01)
```

Rows with the same `aggregate_key` (`dish:<id>`, `user:<id>`) are sent in order; different keys may run in parallel.

---

## 8. Status (07/10/2026)

| Area | Done | Planned |
|---|---|---|
| Local environment | Docker Compose (PostgreSQL 16 + pgvector, 2 roles), user secrets, auto-migrate in Development | — |
| Database | 38 `core` tables matching Data Dictionary 1.1, outbox triggers, lookup seed | Seed data (T137) |
| WebApi | `ApiResponse<T>`, exception middleware, JWT, CORS, rate limit, model-binding errors | `UseForwardedHeaders` before deploying behind Render's proxy; `ApiResponse` body for unknown routes (404/405) |
| Application | Exceptions, `IUnitOfWork`, validator registration | `IJwtService`, `IPasswordService`, `ICurrentUserService`, use cases, controllers (Sprint 1) |
| External services | Cloudinary | Brevo, Google, Recommendation, Mapbox, AI provider |
| Background | — | Outbox worker (T145) |
| Delivery | CI on GitHub Actions: build, test, missing-migration check on every PR to `dev` / `main` (`README.md` → CI) | Render + Vercel staging (T193) |
