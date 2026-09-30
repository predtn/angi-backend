# ANGI — Project Architecture

What the system is and how the pieces fit together. Code-level rules (naming, validation, queries, error mapping) live in `coding_rule.md`; this file does not repeat them.

✅ = exists in the repo · 🔲 = planned

---

## 1. Project Overview

**ANGI (Ăn Gì)** — SEP490_G71 capstone. A web app that recommends Vietnamese **regional dishes** to travelers and builds a **feasible day-by-day food roadmap** (location + travel time). This repo is the **backend REST API**; the frontend is a separate Next.js repo.

**Actors:** Guest · Traveler · Restaurant Owner · Moderator · Admin

**Modules** — use these names for feature folders (`DTOs/<Feature>/`, `Validators/<Feature>/`) and commit scopes:

| Iteration | Modules |
|---|---|
| 1 | Auth, Account, Restaurant, Discovery |
| 2 | Roadmap, Social, Notification |
| 3 | Moderation, Administration, Audit |

Recommendation is built in all three iterations inside a **separate service** (see §2).

**Out of scope:** payment, ordering/delivery, table booking, importing third-party data, native mobile app.

**Performance:** recommendation and roadmap requests must respond within **3 seconds**.

---

## 2. Tech Stack

| Concern | Technology | Status |
|---|---|---|
| Framework | .NET 10, ASP.NET Core Web API (controllers) | ✅ |
| Database | PostgreSQL + EF Core 10 (`Npgsql.EntityFrameworkCore.PostgreSQL`) | ✅ package |
| DB naming | `EFCore.NamingConventions` (snake_case) | ✅ package |
| Validation | FluentValidation | ✅ package |
| Auth | JWT Bearer, BCrypt, Google OAuth | ✅ package · 🔲 config |
| API docs | OpenAPI + Scalar (`/scalar`, Development only) | ✅ |
| Testing | xUnit, Moq, FluentAssertions 7.x | ✅ package |
| Hosting | Render (backend + PostgreSQL), Vercel (frontend) | 🔲 |

### External services 🔲

Each one = an interface in `Application/Common/Interfaces/Services/` + an implementation in `Infrastructure/Services/`.

| Service | Purpose |
|---|---|
| Brevo | Email (verification, reset password) |
| Cloudinary | Image upload |
| Map / routing API | Travel time between roadmap stops (provider TBD) |
| Recommendation service | Ranking of dishes (see below) |

### Recommendation service

A separate service, **called over HTTP/HTTPS**, sharing the **same PostgreSQL database** (own schema, see §6).

- Backend calls it through `IRecommendationService` (typed `HttpClient`). It returns **ranked IDs + scores**; the backend loads the entities and builds the response.
- Timeout well under 3 s. If it fails or times out → **fall back to popularity ranking** from the DB.
- The HTTP contract (endpoints, payloads) is defined in the SDS.

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
| Domain | — | Entities, enums, `BaseEntity` |
| Application | Domain | Use cases, DTOs, validators, **interfaces** (repositories, services) |
| Infrastructure | Application | `ANGIContext`, repositories, external service clients |
| WebApi | Application, Infrastructure | Controllers, middleware, config, `Program.cs` |

Application defines interfaces; Infrastructure implements them. Controllers use only Application interfaces.

---

## 4. Request Flow

```
Client ── HTTPS + JSON (Bearer JWT)
  ▼
ExceptionHandlingMiddleware          catches everything below → ApiResponse (error)
  ▼
CORS → Authentication → Authorization
  ▼
Controller                           call use case, wrap in ApiResponse<T>
  ▼
UseCase
  1. validate (FluentValidation)
  2. business logic (+ ICurrentUserService if needed)
  3. repositories  ──▶ ANGIContext ──▶ PostgreSQL
  4. IUnitOfWork.SaveChangesAsync()  (writes only, once)
  5. map entity → ResponseDto
  ▼
200 / 201  { "success": true, "message": null, "data": { ... } }
```

On error, any layer **throws**; the middleware maps the exception to a status code (`coding_rule.md` §7) and returns `{ "success": false, "message": "...", "data": null }`.

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
| `AddInfrastructure(config)` | `ANGIContext`, repositories, `IUnitOfWork`, external service clients |
| `AddWebApi()` | Controllers, OpenAPI, JWT, CORS, `ExceptionHandlingMiddleware` |

### Configuration

`appsettings.json` sections (names not final):

```json
{
  "ConnectionStrings": { "Default": "" },
  "Jwt":            { "Issuer": "", "Audience": "", "SecretKey": "", "AccessTokenMinutes": 60 },
  "Cors":           { "AllowedOrigins": [ "http://localhost:3000" ] },
  "Google":         { "ClientId": "" },
  "Brevo":          { "ApiKey": "", "SenderEmail": "" },
  "Cloudinary":     { "CloudName": "", "ApiKey": "", "ApiSecret": "" },
  "Recommendation": { "BaseUrl": "", "ApiKey": "", "TimeoutSeconds": 2 }
}
```

**Never commit secrets.** Local: `dotnet user-secrets` or `appsettings.Local.json` (git-ignored). Render: environment variables, e.g. `ConnectionStrings__Default`, `Jwt__SecretKey`.

---

## 6. Database

### Setup 🔲

```csharp
services.AddDbContext<ANGIContext>(options =>
    options.UseNpgsql(configuration.GetConnectionString("Default"))
           .UseSnakeCaseNamingConvention());
```

Local connection string: `Host=localhost;Port=5432;Database=angi;Username=postgres;Password=<secret>`

### Conventions

| Topic | Rule |
|---|---|
| Naming | snake_case, generated automatically: `RestaurantItem` → `restaurant_items`, `PasswordHash` → `password_hash`. Never rename by hand. |
| Primary key | `Guid` → `uuid` |
| Date/time | Always **UTC** (`DateTime.UtcNow`) → `timestamptz`. Npgsql throws on local time. |
| Location | Two columns `Latitude`, `Longitude` (`double`) + index on both. "Nearby" = bounding-box filter, then Haversine. Travel time comes from the map API. No PostGIS. |
| Enums | Stored as strings (`coding_rule.md` §11) |

### BaseEntity 🔲

```csharp
public abstract class BaseEntity
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}
```

- Timestamps are set automatically in `ANGIContext.SaveChangesAsync`. Use cases never set them.
- **Soft delete:** delete = set `IsDeleted = true`. A global filter `HasQueryFilter(e => !e.IsDeleted)` hides deleted rows.
- Unique indexes must skip deleted rows: `.IsUnique().HasFilter("is_deleted = false")`.
- Audit logs (FE-03) use a separate `audit_logs` table.

### Migrations

```bash
dotnet tool install --global dotnet-ef   # once

dotnet ef migrations add <Name> -p ANGI.Infrastructure -s ANGI.WebApi -o Persistences/Migrations
dotnet ef database update -p ANGI.Infrastructure -s ANGI.WebApi
```

One migration per feature change, PascalCase name (e.g. `AddRestaurant`). Never edit generated files.

### Shared database with the recommendation service

One database, **two schemas**:

| Schema | Owner | Contents |
|---|---|---|
| `public` | Backend (EF Core migrations) | Users, dishes, restaurants, interactions, roadmaps |
| `recommendation` | Recommendation service (its own migrations) | Vectors (`pgvector`), scores |

| Access | Allowed |
|---|---|
| Service reads `public` (incl. `JOIN` with its tables) | ✅ |
| FK `recommendation.* → public.*` | ✅ recommended |
| Service writes `public` | ❌ |
| Backend reads or writes `recommendation` | ❌ — ask the service over HTTP |

Rules:

- **EF Core does not map `recommendation.*`**, otherwise migrations would try to create or drop those tables.
- **Renaming or dropping a `public` column the service reads is a breaking change** — mention it in the PR and tell the service owner.
- The service must add `WHERE is_deleted = false` itself; EF Core's soft-delete filter does not apply to its SQL.
- `pgvector` is enabled by the service's migrations: `CREATE EXTENSION IF NOT EXISTS vector;`

```sql
-- example query run by the service
SELECT d.id
FROM recommendation.dish_vectors v
JOIN public.dishes d ON d.id = v.dish_id
WHERE d.is_deleted = false
ORDER BY v.embedding <=> $1      -- cosine distance to the user vector
LIMIT 10;
```
