# ANGI — Project Architecture

What the system is and the contracts the backend must respect. How to write the code: `coding_rule.md`. Git, commits and pull requests: `commit_guide.md`.

## 0. Sources of truth

The design documents decide; these `.agents` files summarize them for coding. Always use the file with the highest version number.

| Document | Decides |
|---|---|
| `.docs/ANGI_API_Design_Ver*.xlsx` | Every endpoint (request, response, status, error codes), the internal Reco API, sheet "Mã lỗi" (error codes), sheet "Enum" (allowed values), sample JSON |
| `.docs/ANGI_Data_Dictionary_Ver*.xlsx` | Every table and column of schemas `core` and `recommendation`, with types, nullability, defaults and CHECKs |
| `.docs/ANGI_Jira_Plan_Ver*.xlsx` | Task keys (`ANGI-123`) for branches and commits |
| `angi-reco` repo, `.docs/Recommendation_System_Design_Ver*.docx` | Recommendation algorithm and the backend ↔ reco split |

- Implement exactly what the documents say. Before coding an endpoint, read its rows in sheets "Backend API" and "Chi tiết Request-Response"; before touching a table, read it in the Data Dictionary.
- When the documents are silent, ambiguous or contradict each other, or the work needs something they do not contain (a new error code, enum value, column, endpoint field), stop and ask. The documents are updated first, then the code, in the same pull request.
- When these `.agents` files disagree with the documents, the documents win; fix the `.agents` file.

## 1. Product

**ANGI (Ăn Gì)** recommends Vietnamese regional dishes to travelers and builds a day-by-day food roadmap. This repo is the backend REST API; the frontend (Next.js) and the recommendation service (FastAPI, `angi-reco`) are separate repos.

- Actors: Guest, Traveler, Restaurant Owner, Mod, Admin.
- Out of scope, never build: payment, ordering or delivery, table booking, third-party data import, native mobile app.
- Recommendation and roadmap requests answer within 3 s.

**Modules.** These names are the `<Module>` folders in the code (`coding_rule.md` §1) and, lowercase, the commit scopes: `Auth`, `Account`, `Restaurant`, `Discovery`, `Roadmap`, `Social`, `Notification`, `Moderation`, `Administration`, `Audit`, `Media`. `Media` is MEDIA-01 (module "Media" in the API Design): one upload used by every other module, which then refers to the file by `mediaId`.

## 2. Tech stack

| Concern | Technology |
|---|---|
| Framework | .NET 10, ASP.NET Core Web API with controllers |
| Database | PostgreSQL 16 + pgvector; EF Core 10 (`Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`) |
| Validation | FluentValidation |
| Auth | JWT Bearer, rotating refresh token, BCrypt, Google sign-in |
| API docs | OpenAPI + Scalar at `/scalar`, Development only |
| Tests | xUnit, Moq, FluentAssertions 7.x (never 8.x: paid for commercial use) |
| Hosting | Render (backend, reco, PostgreSQL), Vercel (frontend) |

Do not add another framework or library for a concern this table already covers.

## 3. Solution structure

```
Domain  ◀──  Application  ◀──  Infrastructure
                  ▲                  ▲
                  └──── WebApi ──────┘   (Infrastructure: DI registration only)
Test ──▶ all projects
```

| Project | Contains |
|---|---|
| Domain | Entities, enums, `BaseEntity`, timestamp and soft-delete interfaces. No dependencies. |
| Application | Use cases, DTOs, validators, exceptions, and the interfaces of repositories and services |
| Infrastructure | `ANGIContext`, EF configurations, migrations, repositories, external service clients |
| WebApi | `Program.cs`, controllers, middlewares, `Configs/` (JWT, CORS, rate limit), `ApiResponse<T>` |

Application declares interfaces, Infrastructure implements them, controllers use only Application interfaces. Layer rules: `coding_rule.md` §1–2.

## 4. Request pipeline

Order in `Program.cs`. Keep it; a new middleware goes where its dependencies are already available.

```
ExceptionHandlingMiddleware      every exception → ApiResponse error
Status code pages                empty 404 / 405 → ApiResponse ROUTE_NOT_FOUND / METHOD_NOT_ALLOWED
[Development] migrate database, OpenAPI + Scalar (/scalar)
HTTPS redirection
CORS (policy "Frontend")
RateLimitPartitionMiddleware     reads the email of login / resend / forgot-password for the rate-limit key
LoginFailureLimitMiddleware      5 INVALID_CREDENTIALS per email + IP in 15 min → 429 until the window ends; a success clears the count
Authentication (JWT)             401 UNAUTHORIZED as ApiResponse
Rate limiter                     resend / forgot-password and general limits → 429 TOO_MANY_REQUESTS + Retry-After
Authorization                    403 FORBIDDEN as ApiResponse
AccountStatusMiddleware          endpoints that need a token: account not active → 403 with the AUTH-04 error codes
Controllers → use case → repositories → ANGIContext → PostgreSQL (schema core)
```

## 5. API conventions

These apply to every endpoint unless its row in the API Design says otherwise.

| Topic | Rule |
|---|---|
| Base URL, JSON | `/api/v1`, camelCase, every response wrapped in `ApiResponse<T>` (`coding_rule.md` §5) |
| Tokens | Access token 15 min; refresh token 30 days, rotated on every use (`core.user_sessions`). Claims: `sub`, `role`, `email_verified` |
| Access | Column "Auth / Role" of the API Design: `Public` needs no token; `Public (token tùy chọn)` personalizes when a token is sent; a named role allows only that role (403 otherwise). An account that is not `active` is rejected on every endpoint that needs a token, even with a valid access token, with the same codes as AUTH-04 (`ACCOUNT_SUSPENDED` + `suspendedUntil`, `ACCOUNT_BANNED`, `ACCOUNT_DEACTIVATED`, `EMAIL_NOT_VERIFIED`). The status is read from the database with a 30 s cache (`IAccountStatusCache`); every use case that changes `users.status` calls `Invalidate(userId)` |
| Mod / Admin permissions | Effective permissions = role default + allow − deny (`core.user_permissions`), read from the database with a short cache, never from the JWT. Admin has every permission |
| Paging | `page` (from 1), `pageSize` (default 20, max 100) → `Paged<T>`. Lists ranked by reco use `cursor` + `limit` and return `requestId` |
| Time, money, ids | ISO 8601 UTC; dates `yyyy-MM-dd`; business hours `HH:mm` Vietnam time; integer VND; `int` / `long` ids, `uuid` only for `requestId` and `eventUuid` |
| Delete | Soft delete for users, restaurants, roadmaps, blogs, comments; 200 with `data: null` |
| Rate limit | Login 5 failures / 15 min per email + IP; resend and forgot-password 1 / 60 s; everything else 100 / min per user (per IP when anonymous) |

## 6. Database

### Schemas

| Schema | Owner role | Changed by |
|---|---|---|
| `core` | `angi_backend` (this backend) | EF Core migrations in this repo |
| `recommendation` | `angi_reco` (reco service) | Alembic in `angi-reco` |
| `public` | none | holds the `citext` and `vector` extensions only |

- Each role reads and writes only its own schema. The backend never maps or queries `recommendation.*`; it asks reco over HTTP.
- No foreign key crosses schemas. `user_id` / `dish_id` in `recommendation` are `core.users.id` / `core.dishes.id`: never reuse or renumber those ids.
- `core.audit_logs` is append-only: trigger `trg_audit_logs_append_only` rejects UPDATE, DELETE and TRUNCATE for every role, the owner included. How to write it: `coding_rule.md` §17.
- Local setup (Docker, roles, connection string): `README.md`.

Column, entity and migration rules: `coding_rule.md` §15–16.

## 7. Outbox (backend → reco)

Dish changes and likes / dislikes must reach reco even when it is down, and never twice. Database triggers (migration `InitialCreate`) write `core.outbox_messages` in the same transaction, so use cases only write `dishes` / `dish_feedbacks` and never insert outbox rows themselves.

| Change | `event_type` | Reco endpoint |
|---|---|---|
| Dish inserted active; name, description or form changed; shown again | `DISH_UPSERTED` | `POST /dishes/upsert` |
| Dish goes from active to hidden or removed | `DISH_DEACTIVATED` | `DELETE /dishes/{dish_id}` |
| Like / dislike inserted (same `event_uuid` as `dish_feedbacks`) | `FEEDBACK_CREATED` | `POST /feedback` |

`is_available` and `price` changes write nothing. Payloads match the Reco API request bodies.

The worker is a `BackgroundService`:

```
poll pending rows → lock a batch (locked_until) → call reco with Idempotency-Key = event_uuid
  2xx                       → done, processed_at
  5xx / timeout             → attempt_count++, next_attempt_at = backoff
  4xx or MaxAttempts        → dead (visible to Admin, OPS-01)
```

Rows with the same `aggregate_key` (`dish:<id>`, `user:<id>`) are sent in order; different keys may run in parallel.

## 8. Integrations

Every external service is an interface in `Application/Common/Interfaces/Services/` and an implementation in `Infrastructure/Services/`. Where the table has no interface name yet, choose `I<Purpose>Service` and add it here in the same pull request.

| Service | Interface | Purpose |
|---|---|---|
| Cloudinary | `ICloudinaryService` | Image and document upload (`core.media_files`); private files get signed URLs valid 5 min |
| Recommendation service | `IRecommendationService` | Survey, ranking, impressions, survey status (sheet "Reco API (nội bộ)") |
| Brevo | `IEmailService` | Email: verification, password reset |
| Google | — | Verify Google ID tokens (`Google.Apis.Auth`) |
| Mapbox Directions | `IRouteService` | Distance and time of each leg of a roadmap day (RM-14) |
| AI provider | — | Roadmap generation (`core.roadmap_generation_jobs`) |

### Recommendation service

- Typed `HttpClient`, header `X-Api-Key`, snake_case JSON. Endpoints: sheet "Reco API (nội bộ)".
- Responses are not wrapped in `ApiResponse`: a 2xx body is the DTO itself; an error body is `{ "error_code": "...", "message": "...", "errors": { field: [messages] } }` (`errors` only for 422 `VALIDATION_FAILED`). Branch on `error_code`, never on `message`.
- The backend filters candidates (meal, form, price, tags, distance, active dish of a verified, visible, open restaurant) and sends at most 2000, sorted by restaurant rating, then dish `like_count`. Reco only scores them (stable sort, so ties keep that order) and returns ranked ids, scores and `request_id`; the backend loads the entities and builds the response.
- `/recommendations` timeout 800 ms. On 5xx or timeout, order by restaurant rating and return `requestId = null`.
- Survey, `/recommendations`, `/impressions` and survey status are called directly from the use case. Dish changes and likes / dislikes go only through the outbox (§7).

### Email (Brevo)

- Typed `HttpClient` to the Brevo transactional API: `POST {BaseUrl}smtp/email`, header `api-key`. No Brevo SDK.
- One method per email; the use case passes the recipient, display name, raw token and how long the token is valid. The service builds the link and the Vietnamese template (HTML and plain text) in Infrastructure.
- Links (sheet "Backend API", AUTH-01 / AUTH-03 / AUTH-08): `{Frontend:BaseUrl}/verify-email?token=<token>` (the page calls AUTH-02) and `{Frontend:BaseUrl}/reset-password?token=<token>` (the page calls AUTH-09). The token is URL-encoded; the display name is HTML-encoded.
- Missing configuration, a non-2xx answer, a network error or a timeout → `ServiceUnavailableException` 503 `SERVICE_UNAVAILABLE`. The API still starts without Brevo keys; only sending fails. Never log the token, the link or the email body.

### Mapbox Directions (RM-14)

- One Directions call per day (`/directions/v5/mapbox/{profile}/{lng,lat;...}`), at most 25 points; read `routes[0].legs[].distance` (m) and `.duration` (s). Profiles `driving` (default), `walking`, `cycling`.
- Points: origin (if any) → the day's items by meal slot, then `sortOrder`. Origin = GPS sent by the app (`originLat` and `originLng` together), else the pinned point, else none. Skip the first leg and return `originSkipped = true` when the origin is more than 30 km (straight line) from the first item. A day with fewer than 2 points returns empty `legs`.
- Cache in `IMemoryCache` for `CacheHours`, key = profile + coordinates (origin rounded to 3 decimals ≈ 100 m). Timeout 3 s; on failure estimate straight line × 1.3 at 25 / 12 / 4 km/h and return `isEstimated = true`.
- Travel distance and time are never stored. The secret token stays in backend config; the frontend has its own public `pk.*` token. "Open in Google Maps" is a link the frontend builds.

### Roadmap origin

`roadmaps.origin_mode`: `pinned` stores `origin_latitude / origin_longitude / origin_label`; `gps` stores nothing and the app sends coordinates on each RM-10 / RM-14 call; `none` = province only. Coordinates exist if and only if `pinned` (CHECK). Switching away from `pinned` clears the coordinates in the use case (RM-04).

## 9. Configuration and secrets

Composition: `Program.cs` calls only `AddApplication()`, `AddInfrastructure(configuration)`, `AddWebApi(configuration)` (`coding_rule.md` §12).

`appsettings.json` holds one section per configured concern, with non-secret defaults. Use exactly these section and key names; a section that is not in `appsettings.json` yet is added together with the feature that needs it:

| Section | Keys |
|---|---|
| `ConnectionStrings` | `Default` |
| `Jwt` | `Issuer`, `Audience`, `SecretKey` (≥ 32 bytes, validated at startup), `AccessTokenMinutes`, `RefreshTokenDays` |
| `Cors` | `AllowedOrigins` |
| `RateLimit` | `GeneralPermitLimit`, `GeneralWindowSeconds`, `LoginPermitLimit`, `LoginWindowMinutes`, `SensitiveAuthPermitLimit`, `SensitiveAuthWindowSeconds` |
| `Cloudinary` | `CloudName`, `ApiKey`, `ApiSecret` |
| `Recommendation` | `BaseUrl`, `ApiKey` (same value as `RECO_API_KEY` of angi-reco), `TimeoutMilliseconds` (800) |
| `Google` | `ClientId` |
| `Brevo` | `BaseUrl` (`https://api.brevo.com/v3/`), `ApiKey`, `SenderEmail`, `SenderName` (`ANGI`), `TimeoutMilliseconds` (10000) |
| `Frontend` | `BaseUrl` (`http://localhost:3000`): base of the links sent by email |
| `Mapbox` | `BaseUrl`, `AccessToken`, `TimeoutMilliseconds` (3000), `CacheHours` (24) |
| `Outbox` | `PollSeconds` (2), `BatchSize` (50), `MaxAttempts` (10) |

- Never commit a secret. A secret key stays empty in `appsettings.json`.
- Local values: `dotnet user-secrets set "<Section>:<Key>" "<value>" --project ANGI.WebApi`. The app needs `ConnectionStrings:Default` and `Jwt:SecretKey` to start.
- Render: environment variables with `__`, e.g. `ConnectionStrings__Default`.
