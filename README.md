# ANGI Backend

REST API for **ANGI (Ăn Gì)**, a capstone project that recommends Vietnamese regional dishes to travelers and builds day-by-day food roadmaps.

The backend owns accounts, restaurants, menus, roadmaps, social features, moderation and the AI food assistant. It is one of three services:

| Repository | Role |
|---|---|
| [angi-frontend](https://github.com/Tulas2411/angi-frontend) | Next.js web app; calls this API from its server |
| **angi-backend** (this repo) | ASP.NET Core API, schema `core` |
| [angi-reco](https://github.com/predtn/angi-reco) | FastAPI recommendation service, schema `recommendation`; called by this API |

## Tech stack

| Concern | Technology |
|---|---|
| Runtime | .NET 10, ASP.NET Core |
| Data | PostgreSQL 16 + pgvector, EF Core (Npgsql), snake_case naming |
| Auth | JWT bearer with rotating refresh tokens, Google sign-in |
| Validation | FluentValidation |
| External services | Cloudinary (media), Brevo (email), Mapbox (directions), OpenAI (Agent, AI roadmaps) |
| API docs | OpenAPI + Scalar (Development) |
| Tests | xUnit, FluentAssertions, Moq, `WebApplicationFactory` |

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker Desktop (Docker Compose v2)

### 1. Start the database

```bash
cp .env.example .env          # once; change passwords or port here if needed
docker compose up -d --wait
```

This starts PostgreSQL with the `core` and `recommendation` schemas and their roles. Details: [local database](docs/local-database.md).

### 2. Set the required secrets

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=angi;Username=angi_backend;Password=backend_dev" --project ANGI.WebApi
dotnet user-secrets set "Jwt:SecretKey" "<at least 32 random characters>" --project ANGI.WebApi
```

These two are enough to start. Uploads, email, Google sign-in, routes, recommendations and the Agent each need their own key: see [configuration](docs/configuration.md).

### 3. Run

```bash
dotnet run --project ANGI.WebApi
```

| URL | |
|---|---|
| `http://localhost:5154/api/v1` | API base |
| `http://localhost:5154/scalar` | Interactive API reference (Development only) |
| `http://localhost:5154/health` | Health check |

In Development, pending EF migrations are applied on startup.

## Testing

```bash
dotnet build ANGI.slnx -c Release
dotnet test ANGI.slnx -c Release --no-build
dotnet ef migrations has-pending-model-changes -p ANGI.Infrastructure -s ANGI.WebApi --configuration Release --no-build
```

These are the checks CI runs on every pull request. PostgreSQL integration tests are opt-in locally and need a dedicated test database: see [testing](docs/testing.md).

## Project structure

```text
ANGI.Domain/             Entities and enums
ANGI.Application/        Use cases, services and DTOs, grouped by module
ANGI.Infrastructure/     EF Core context, configurations, migrations, repositories, external clients
ANGI.WebApi/             Controllers, middleware, filters, startup
ANGI.UnitTests/          Unit tests
ANGI.IntegrationTests/   HTTP contract and PostgreSQL end-to-end tests
docker/                  PostgreSQL init scripts (roles, schemas, extensions)
.agents/                 Architecture, coding rules and Git workflow
docs/                    Design documents (API Design, Data Dictionary, Jira plan, Agent contracts) and guides
```

## Deployment

Production runs on Render as a Docker web service with migrations applied by a pre-deploy step and a single instance. See [deploy](docs/deploy.md).

## Documentation

| Topic | Where |
|---|---|
| Endpoints, error codes, enums | `docs/ANGI_API_Design_Ver*.xlsx` |
| Tables and columns | `docs/ANGI_Data_Dictionary_Ver*.xlsx` |
| Architecture and module boundaries | [.agents/project_architecture.md](.agents/project_architecture.md) |
| Coding rules | [.agents/coding_rule.md](.agents/coding_rule.md) |
| Configuration and secrets | [docs/configuration.md](docs/configuration.md) |
| Local database | [docs/local-database.md](docs/local-database.md) |
| Testing and CI | [docs/testing.md](docs/testing.md) |
| Deploy (Render) | [docs/deploy.md](docs/deploy.md) |
| AI food assistant (Agent) | [docs/agent.md](docs/agent.md) |

The design documents in `docs/` are the source of truth: when code and documents disagree, the documents win, and both change in the same pull request.

## Contributing

Branches, commit messages and pull requests follow [.agents/commit_guide.md](.agents/commit_guide.md). Open pull requests against `dev`; a pull request is merged only when the `build-test` check is green.

## License

[MIT](LICENSE)
