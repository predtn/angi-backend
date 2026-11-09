# Testing and CI

## CI

GitHub Actions (`.github/workflows/ci.yml`) runs on every pull request to `dev` or `main` and on every push to them. A pull request can be merged only when the `build-test` check is green.

| Step | Fails when |
|---|---|
| Build (Release) | The solution does not compile |
| Test | A unit, HTTP contract or PostgreSQL integration test fails (CI provides its own PostgreSQL; only the live Cloudinary and Brevo tests are skipped) |
| Check for missing EF migration | An entity or configuration changed without `dotnet ef migrations add` |

Run the same checks locally before pushing:

```bash
dotnet build ANGI.slnx -c Release
dotnet test ANGI.slnx -c Release --no-build
dotnet ef migrations has-pending-model-changes -p ANGI.Infrastructure -s ANGI.WebApi --configuration Release --no-build
```

The last command needs the user secrets `ConnectionStrings:Default` and `Jwt:SecretKey` (any value); it does not connect to the database. Test results (`.trx`) of each run are attached to the run as the `test-results` artifact.

## Test projects

```bash
dotnet test ANGI.UnitTests/ANGI.UnitTests.csproj
dotnet test ANGI.IntegrationTests/ANGI.IntegrationTests.csproj
```

HTTP contract tests run by default. PostgreSQL tests require an explicitly selected database whose name contains `test`. Create it once in the local container (owned by `angi_backend`, which can then create the `citext` and `vector` extensions itself):

```bash
docker exec angi-postgres psql -U postgres -c "create database angi_test owner angi_backend"

RUN_POSTGRES_INTEGRATION_TESTS=1 \
ANGI_TEST_POSTGRES_CONNECTION_STRING="Host=localhost;Port=5432;Database=angi_test;Username=angi_backend;Password=<password>" \
dotnet test ANGI.IntegrationTests/ANGI.IntegrationTests.csproj
```

The PostgreSQL suite starts `ANGI.WebApi` in memory with `WebApplicationFactory`, sends real HTTP requests through the middleware and controller pipeline, and verifies the resulting rows with a fresh EF Core context. It resets the `core` schema before each end-to-end scenario, so **never point it at a development or production database**. CI provisions its own PostgreSQL service and runs this suite automatically.

Live tests against external providers are skipped unless enabled explicitly; see [checking the Brevo key](configuration.md#checking-the-brevo-key).

`artifacts/` contains temporary build output and local diagnostic programs. It is ignored by Git and is not part of the application source. Durable automated tests belong in `ANGI.UnitTests` or `ANGI.IntegrationTests`.

## End-to-end coverage

| Tasks | Integrated workflow |
|---|---|
| ANGI-17 | Multipart media upload, HTTP response, and persisted media metadata |
| ANGI-23 | Exception middleware response and uniqueness state in PostgreSQL |
| ANGI-29, ANGI-58 | Registration, verification email token, activation response, and persisted user/token state |
| ANGI-33 | Login, refresh rotation, logout, and persisted session revocation |
| ANGI-39, ANGI-42 | Restaurant registration, retrieval, update, audit log, persisted profile, and gallery edits sent back from the returned `imageMediaIds` |
| ANGI-53, ANGI-76 | Dish availability and complete menu-submission lifecycle |
| ANGI-62 | Google login with a deterministic identity boundary and persisted account/link/session |
| ANGI-66, ANGI-70 | Forgot/reset password, consumed token, changed password, and revoked sessions |
| ANGI-75 | Private document upload and persisted verification aggregate |
| ANGI-230 | Real typed-client DI and HTTP transport against a loopback Mapbox server, including response mapping and cache |
| ANGI-191 | Moderator restaurant filters and detail, visibility changes, dish outbox events, atomic report resolution, sanctions, expiry, rollback and concurrent decisions |
