# angi-backend
Backend for ANGI-Capstone Project

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

The last command needs the user secrets from [Local database](#local-database) (or any value for `ConnectionStrings:Default` and `Jwt:SecretKey`); it does not connect to the database. Test results (`.trx`) of each run are attached to the run as the `test-results` artifact.

Unit and integration tests are separate projects:

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

The PostgreSQL suite starts `ANGI.WebApi` in memory with `WebApplicationFactory`, sends real HTTP requests through the middleware and controller pipeline, and verifies the resulting rows with a fresh EF Core context. It resets the `core` schema before each end-to-end scenario, so never point it at a development or production database. CI provisions its own PostgreSQL service and runs this suite automatically.

Current end-to-end coverage for the tracked tasks:

| Tasks | Integrated workflow |
|---|---|
| ANGI-17 | Multipart media upload, HTTP response, and persisted media metadata |
| ANGI-23 | Exception middleware response and uniqueness state in PostgreSQL |
| ANGI-29, ANGI-58 | Registration, verification email token, activation response, and persisted user/token state |
| ANGI-33 | Login, refresh rotation, logout, and persisted session revocation |
| ANGI-39, ANGI-42 | Restaurant registration, retrieval, update, audit log, and persisted profile |
| ANGI-53, ANGI-76 | Dish availability and complete menu-submission lifecycle |
| ANGI-62 | Google login with a deterministic identity boundary and persisted account/link/session |
| ANGI-66, ANGI-70 | Forgot/reset password, consumed token, changed password, and revoked sessions |
| ANGI-75 | Private document upload and persisted verification aggregate |
| ANGI-230 | Real typed-client DI and HTTP transport against a loopback Mapbox server, including response mapping and cache |
| ANGI-191 | Moderator restaurant filters and detail, visibility changes, dish outbox events, atomic report resolution, sanctions, expiry, rollback and concurrent decisions |

Temporary account suspensions are expired by `AccountSuspensionExpiryWorker` once per minute. It invokes the scoped expiry use case in batches of 100; PostgreSQL `SKIP LOCKED` prevents overwriting concurrent sanctions. Each batch restores active status, clears the deadline, commits, and invalidates the account-status cache. Revoked sessions remain revoked.

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
| `Recommendation:BaseUrl` | Recommendation service; locally `http://127.0.0.1:8000`, not `localhost`: uvicorn listens on IPv4 only and Windows first tries `localhost` over IPv6, which takes about 2 s to fail, past the 800 ms timeout | Survey and recommendation features |
| `Recommendation:ApiKey` | Header `X-Api-Key`; must equal `RECO_API_KEY` in the `.env` of angi-reco | Survey and recommendation features |
| `Cloudinary:CloudName`, `Cloudinary:ApiKey`, `Cloudinary:ApiSecret` | Image and document upload | Upload features and the Cloudinary live tests |
| `Google:ClientId` | Verifying Google ID tokens; same client id as the frontend | Google sign-in |
| `Brevo:ApiKey` | Verification and password-reset emails | Sending emails: without it the API starts, but every email returns 503 `SERVICE_UNAVAILABLE` |
| `Mapbox:AccessToken` | Mapbox Directions (RM-14); the secret token, not the frontend's public `pk.*` token | Roadmap routes |
| `Ai:ApiKey` | OpenAI key for AI roadmap generation (RM-10) | Optional: without it every day is scheduled by the job's own fallback |

Set each one once:

```bash
dotnet user-secrets set "Recommendation:BaseUrl" "http://127.0.0.1:8000" --project ANGI.WebApi
dotnet user-secrets set "Recommendation:ApiKey" "<RECO_API_KEY of angi-reco>" --project ANGI.WebApi
dotnet user-secrets set "Cloudinary:CloudName" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Cloudinary:ApiKey" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Cloudinary:ApiSecret" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Google:ClientId" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Brevo:ApiKey" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Mapbox:AccessToken" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Ai:ApiKey" "<value>" --project ANGI.WebApi
dotnet user-secrets list --project ANGI.WebApi    # check what is set
```

On Render the same keys are environment variables with `__` instead of `:` (`Jwt__SecretKey`, `Recommendation__ApiKey`).

`appsettings.json` already holds the non-secret defaults: `Brevo:BaseUrl`, `Brevo:SenderEmail` (`angi.sep490.g71@gmail.com`, the team's verified Brevo sender; override it only if your Brevo account uses another verified sender), `Brevo:SenderName` (`ANGI`), `Brevo:TimeoutMilliseconds`, and `Frontend:BaseUrl` (`http://localhost:3000`), the base of the links in the emails (`/verify-email?token=...`, `/reset-password?token=...`). On Render set `Frontend__BaseUrl` to the Vercel URL.

To check the Brevo keys, send both emails to your own address (the live test is skipped unless both variables are set):

```bash
RUN_BREVO_LIVE_TESTS=1 BREVO_LIVE_TEST_TO=you@example.com dotnet test ANGI.IntegrationTests/ANGI.IntegrationTests.csproj --filter BrevoLiveTests
```

## Agent chat-only frontend

The Agent UI needs a chat input and a Send button. Open a conversation with `POST /api/v1/agent/conversations` and `{}` (or an owned `roadmapId` to edit a saved trip). Then send the authenticated Traveler's messages with only `content`:

```http
POST /api/v1/agent/conversations/7/messages
Content-Type: application/json
Authorization: Bearer <access-token>
```

```json
{
  "content": "Tôi muốn lên lịch ăn uống Hà Nội 1 ngày, ngân sách 500000 đồng"
}
```

Omit `clientMessageId` and `expectedConversationVersion` (or send null) to let the backend allocate an unused positive ID and use the current conversation revision under its lock. Every request without an ID is a new message, including repeated text. The response returns `data.clientMessageId` and `data.conversationVersion`. GET conversation returns `data.version`.

For retry-safe clients, supply a fresh positive `clientMessageId` per new message and reuse the same ID/content when retrying it. An explicit ID reused with different business content still returns `MESSAGE_ID_REUSED`; an explicit stale version still returns `CONVERSATION_VERSION_CONFLICT`. If a request without an ID times out, reload the conversation before sending again because automatic retries are new turns and can consume provider quota. The frontend does not classify intent or fill `command`, `selectedItemKey` or `confirmedState`; those optional fields remain compatible for existing clients.

The backend interprets ordinary planning, item edits and requests for another option. `bỏ bản nháp` discards the preview while keeping the official roadmap. `làm lại từ đầu` starts a fresh planning context and detaches the previous saved roadmap without deleting it. Ambiguous item references are clarified in chat.

To save a reviewed preview, send `lưu lịch trình này` or `chốt lịch này` as a new message. The backend chooses the current owned proposal, rechecks catalog prices, expiry and the source roadmap timestamp, then saves the roadmap and chat acknowledgement atomically. Success includes `data.commit` (`roadmapId`, `roadmapUpdatedAt`, `committedProposalId`, `itemIds`). Replaying a message with the same explicit ID returns its stored commit result. A bare `đồng ý`, a quoted instruction or a negated save sentence never commits a roadmap. Asking `hãy lập lịch giúp tôi để chốt lịch` returns the existing ready preview for review without saving it. The explicit `/confirm` endpoint remains available for existing clients.

When a requested change relaxes existing constraints, the backend first asks a confirmation question and stores `state.pendingStateChange`. Reply `xác nhận thay đổi` (or `đồng ý`) to apply those exact pending constraints and generate an updated preview in that turn, or `giữ nguyên` to reject them. Confirmation of constraints does not save a roadmap. A validated updated preview supersedes the previous preview; provider failures preserve the previous persisted state and preview. Server-managed `pendingStateChange`, `sourceRoadmapUpdatedAt` and `historyStartSequence` are response/context metadata; clients do not send them back.

### Roadmap change detection

`roadmaps.updated_at` replaces the former roadmap `version` counter. PostgreSQL generates a UTC timestamp with microsecond precision on every parent write and every insert/update/delete of a roadmap day or item. Moving a child updates both parents. Each update uses `GREATEST(clock_timestamp(), previous_updated_at + interval '1 microsecond')`, so rapid writes and backwards clock adjustments cannot reuse a token. These changes roll back with their transaction. EF reads the final parent timestamp after saving children before mapping responses.

An update proposal stores `baseRoadmapUpdatedAt`; conversation state tracks `sourceRoadmapUpdatedAt`; a commit returns `roadmapUpdatedAt`. Confirmation locks the owned parent row and compares its exact `updated_at` with the snapshot inside the write transaction. A mismatch returns HTTP 409 `ROADMAP_UPDATED_AT_CONFLICT`; request a fresh preview. Background roadmap generation performs the same comparison before replacing items. Conversation and proposal versions retain their separate purposes.

Treat these timestamp strings as opaque concurrency tokens. Keep the original ISO 8601 UTC value, for example `2026-10-29T02:00:00.123456Z`; do not pass it through JavaScript `Date` or formatting that drops microseconds. Agent snapshots are server-managed, so the frontend does not need to construct or echo their timestamps for confirmation.

Migration `20261029020050_ReplaceRoadmapVersionWithUpdatedAt` removes the roadmap counter columns and converts nested JSON in existing proposals, conversation state and message replay responses. Existing uncommitted previews are superseded, in-flight attempts are released, and clients must reload their conversation and obtain a fresh preview. Roadmaps, messages and committed results are retained. Historical committed timestamps that cannot be recovered use the recorded `committed_at` for display; they never authorize a new write. Apply this migration with old API/worker processes stopped. Downgrading requires a pre-migration backup because the removed counters cannot be reconstructed.

Trip planning requires province, duration and budget; `startDate` is optional. Every proposal response contains the complete snapshot ordered by day, meal and sort order, including restored older proposals. Navigation focus is cleared after a successful edit. The provider receives actual catalog tags; unsupported taste preferences remain trip notes rather than invented tag guarantees.

Agent provider calls use `Agent:LlmTimeoutSeconds` (60 seconds by default), capped by the remaining `Agent:TimeoutSeconds` turn budget (90 seconds). `Agent:LeaseSeconds` remains 120 seconds. Other AI consumers keep their independent `Ai:TimeoutSeconds` setting.

Provider HTTP 500/502/503 permits at most two retries with backoff. A classified per-minute limit may retry using a short provider retry hint when the turn has enough remaining time; daily quota exhaustion is never retried. Timeout, network, authentication, rejected requests, refusal and truncated output do not trigger automatic retries. Schema or proposal-operation errors permit one repair within the same call/time budget. Tools are disabled after the allowed rounds so the model must finish or clarify. The default total provider-call budget is eight.

Provider rate/quota limits return HTTP 429 `AGENT_UNAVAILABLE`; other provider failures return HTTP 503 with a safe Vietnamese explanation. The application's own per-user quota returns HTTP 429 `AGENT_RATE_LIMITED`. `Retry-After` uses either HTTP headers or Gemini's structured retry metadata. Gemini object and array error envelopes are both supported. Logs retain only fixed diagnostic labels, status, timings and retry hints, never API keys, raw prompts or provider error messages. Canceling the frontend request propagates cancellation without repair, and failed turns preserve the previous preview.

The CORS policy exposes `Retry-After` to the configured frontend origins so browser clients can read the provider's retry guidance.

`artifacts/` contains temporary build output and local diagnostic programs. It is ignored by Git and is not part of the application source to commit or push. Durable automated tests belong in `ANGI.UnitTests` or `ANGI.IntegrationTests`.

## Conversation preferences extension (2026-10-29)

Implementation contract and test setup: [Conversation preferences v1](.docs/Agent_Conversation_Preferences_Ver1.0.md).

This section supersedes earlier Agent lifecycle, supported-taste-as-note and preview-impression rules. The API Design
and Data Dictionary contain the matching `Agent Preferences v1` contract sheet.

- A non-null roadmap_id uniquely identifies its conversation. Open returns 201 new or 200 reused/reopened.
  Reset keeps the attached roadmap. Reopen after 7 idle days retains ID but starts fresh context/profile.
- AgentState.tasteTargets holds only user-declared ontology targets on 0/.25/.5/.75/1. Backend-owned
  preferences contains the inherited profile snapshot, computed request vector, ontology version,
  profile update/read timestamps and initialization status. Missing dimensions inherit; never infer tastes from dishes.
- Initialize lazily outside locks; unavailable profile reads stay pending, distinct from a null profile.
  GET /users/{id}/preferences is internal/read-only. Conversation ranking passes vector_source and
  user_vector_override to reco. Algorithms/cold-start are unchanged; chat never increments m.
- Agent commit writes AGENT_IMPRESSION_RECORDED outbox entries in its transaction. Send all committed
  dishes grouped by original request_id after commit, with agent_proposal_id. Never count previews.
  Reco rejects conversation impressions without a commit reference. No synthetic feedback is created.
- Keep attached conversation rows when pruning 30-day payloads. Prune active histories too while
  retaining current proposal/recent replay. Sequence/proposal high-water marks survive pruning;
  expired client-message IDs are rejected with MESSAGE_EXPIRED. Maintenance never renews activity.
- Deploy reco migration/service before backend migration/service. Detect duplicate roadmap conversations
  before the unique index and stop with their IDs; never automatically delete or detach them.
## Agent scoped planning

Backend-only contract: [Agent scoped planning v2](.docs/Agent_Scoped_Planning_Ver2.0.md), also
included in the `Agent Scoped v2` worksheets of the API Design and Data Dictionary.
The existing message/confirm endpoints remain compatible. Agent state now supports day/meal
taste overrides and catalog meal requests; the backend computes per-request vectors while reco
keeps its current scoring/cold-start. Named dishes/restaurants must resolve against the public
catalog. Unknown/ambiguous/unavailable results ask for clarification instead of substitution.
User-visible replies are rendered from validated snapshots and fixed clarification templates.
No frontend or reco change, new database table, or migration is required for this extension.
