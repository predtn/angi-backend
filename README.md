# angi-backend
Backend for ANGI-Capstone Project

## CI

GitHub Actions (`.github/workflows/ci.yml`) runs on every pull request to `dev` or `main` and on every push to them. A pull request can be merged only when the `build-test` check is green.

| Step | Fails when |
|---|---|
| Build (Release) | The solution does not compile |
| Test | A unit test fails (Cloudinary live tests are skipped) |
| Check for missing EF migration | An entity or configuration changed without `dotnet ef migrations add` |

Run the same checks locally before pushing:

```bash
dotnet build ANGI.slnx -c Release
dotnet test ANGI.slnx -c Release --no-build
dotnet ef migrations has-pending-model-changes -p ANGI.Infrastructure -s ANGI.WebApi --configuration Release --no-build
```

The last command needs the user secrets from [Local database](#local-database) (or any value for `ConnectionStrings:Default` and `Jwt:SecretKey`); it does not connect to the database. Test results (`.trx`) of each run are attached to the run as the `test-results` artifact.

## Local database

PostgreSQL 16 + pgvector runs in Docker. One database `angi`, two schemas, two roles (see `.agents/project_architecture.md` §6):

| Role | Schema | Used by |
|---|---|---|
| `angi_backend` | `core` | This backend (EF Core migrations) |
| `angi_reco` | `recommendation` | Recommendation service (Alembic migrations) |

Each role can only create and read tables in its own schema. Extensions `citext` and `vector` are already installed in schema `public`.

### Requirements

- Docker Desktop (Docker Compose v2)
- .NET 10 SDK

### Start

```bash
cp .env.example .env          # once; change passwords or port here if needed
docker compose up -d --wait   # starts angi-postgres and waits until it is healthy
```

Set the backend connection string once (stored outside the repo with user secrets):

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=angi;Username=angi_backend;Password=backend_dev" --project ANGI.WebApi
```

Use the port and password from your `.env` if you changed them. The API also needs a JWT signing key to start (any random value of at least 32 bytes; see [External service keys](#external-service-keys) for the others):

```bash
dotnet user-secrets set "Jwt:SecretKey" "<at least 32 random characters>" --project ANGI.WebApi
```

### Migrations run on startup (Development only)

Running the API in Development (`dotnet run --project ANGI.WebApi`, or F5 in the IDE) applies pending EF migrations before it starts, so after `git pull` you do not need `dotnet ef database update`. Other environments never migrate on startup; they run migrations as a deploy step.

| What you see | Meaning |
|---|---|
| `fail: ... Failed executing DbCommand ... __EFMigrationsHistory` on the first run | Normal on an empty database: EF looks for its history table before creating it |
| `PendingModelChangesWarning: The model for context 'ANGIContext' has pending changes` | You changed an entity or configuration without a migration. Run `dotnet ef migrations add <Name> -p ANGI.Infrastructure -s ANGI.WebApi -o Persistences/Migrations` |
| `Failed to connect to 127.0.0.1:5432` | The database is not running: `docker compose up -d --wait` |

### Connection details

| | Backend | Recommendation service |
|---|---|---|
| Host / Port | `localhost` / `5432` (`POSTGRES_PORT`) | same |
| Database | `angi` | `angi` |
| User / Password | `angi_backend` / `backend_dev` | `angi_reco` / `reco_dev` |
| Default schema | `core` | `recommendation` |

Superuser (only for troubleshooting): `postgres` / `postgres_dev`.

### Common commands

```bash
docker compose stop            # stop, keep data
docker compose up -d --wait    # start again
docker compose logs postgres   # view logs
docker compose down -v         # delete the container AND all local data
```

The init script `docker/postgres/init/01-roles-schemas.sh` runs **only when the data volume is empty**. After changing the script or the role settings in `.env`, run `docker compose down -v` and start again (all local data is lost).

### Troubleshooting

| Problem | Fix |
|---|---|
| `port is already allocated` | Another PostgreSQL uses 5432. Set `POSTGRES_PORT=5433` in `.env` and use that port in the connection string. |
| Log shows `$'\r': command not found` | The script was checked out with CRLF. Run `rm docker/postgres/init/01-roles-schemas.sh && git checkout -- docker/postgres/init/01-roles-schemas.sh`, then `docker compose down -v` and start again. |
| `Copy .env.example to .env first` | The `.env` file is missing. |

## External service keys

The API reads its keys from `appsettings.json`, user secrets and environment variables; it does not read `.env`. Every key below is empty in `appsettings.json` and listed in `.env.example` as a reminder. Get the shared dev keys from the team's private channel and never commit a value.

| Key | Used for | Needed locally |
|---|---|---|
| `ConnectionStrings:Default` | PostgreSQL (role `angi_backend`) | Always ([Start](#start)) |
| `Jwt:SecretKey` | Signing access tokens, at least 32 bytes; a local random value is fine | Always: the API refuses to start without it |
| `Recommendation:BaseUrl` | Recommendation service; locally `http://localhost:8000` | Survey and recommendation features |
| `Recommendation:ApiKey` | Header `X-Api-Key`; must equal `RECO_API_KEY` in the `.env` of angi-reco | Survey and recommendation features |
| `Cloudinary:CloudName`, `Cloudinary:ApiKey`, `Cloudinary:ApiSecret` | Image and document upload | Upload features and the Cloudinary live tests |
| `Google:ClientId` | Verifying Google ID tokens; same client id as the frontend | Google sign-in |
| `Brevo:ApiKey`, `Brevo:SenderEmail` | Verification and password-reset emails | Sending emails |
| `Mapbox:AccessToken` | Mapbox Directions (RM-14); the secret token, not the frontend's public `pk.*` token | Roadmap routes |

Set each one once:

```bash
dotnet user-secrets set "Recommendation:BaseUrl" "http://localhost:8000" --project ANGI.WebApi
dotnet user-secrets set "Recommendation:ApiKey" "<RECO_API_KEY of angi-reco>" --project ANGI.WebApi
dotnet user-secrets set "Cloudinary:CloudName" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Cloudinary:ApiKey" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Cloudinary:ApiSecret" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Google:ClientId" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Brevo:ApiKey" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Brevo:SenderEmail" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Mapbox:AccessToken" "<value>" --project ANGI.WebApi
dotnet user-secrets list --project ANGI.WebApi    # check what is set
```

On Render the same keys are environment variables with `__` instead of `:` (`Jwt__SecretKey`, `Recommendation__ApiKey`).
