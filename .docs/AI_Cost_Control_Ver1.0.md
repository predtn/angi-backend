# Backend AI spending controls v1 — 2026-10-31

## Default and scope

**Default: stopped, all daily/monthly USD and request limits = 0.**
Tests use fake AI responses. Installing, migrating, quoting, or running tests makes no paid provider call.

`ANGI.AiGateway` controls only the backend Agent and roadmap-generation AI clients. Both use the `chat`
workload, with a 16,000 output-token ceiling so multi-day plans are not truncated. This is a ceiling, not a request
for that many tokens. The default is one concurrent call and two admissions/minute across both backend clients.
Multi-round chat may need more than two calls per turn; choose appropriate RPM, call and money limits before enabling it.

**Reco is independent.** Keep its existing provider, credentials, prompts, vector generation, retries and databases.
Do not point reco at this gateway or give it a gateway credential. There is no vector workload, vector credential,
vector budget API, or vector-batch approval API in the gateway.

Only the gateway receives the backend's real OpenAI provider key. Backend receives `angi-chat-...`, and the
operator receives `angi-admin-...`. Generate distinct secrets with at least 32 random bytes after each prefix.
Remove the backend's provider key from backend environments/user-secrets when deploying this mode. Reco's
independently configured provider key is unaffected. Anyone retaining the gateway's real provider key can bypass
its accounting; use a dedicated provider project/key for the backend and appropriate outbound access controls.
Protect remote administration with HTTPS and access control.

Deploy one shared gateway/ledger for the backend provider project. Replicas must share the same ledger;
separate ledger databases do not enforce a team-wide total. Reco usage is not included in gateway totals.

## Reservation and accounting contract

Every request, including caller retries, commits a reservation before sending to OpenAI. A control-row
`SELECT ... FOR UPDATE` serializes admissions, settlements, budget changes and stops across instances.
PostgreSQL stores the admission date, request hash, held USD, measured tokens, conservative charge and outcome.
The gateway has no automatic retry. It rejects streaming, multiple outputs, multimodal content, hosted paid tools,
client-selected service tiers and unapproved models. Backend function tools remain supported.

Allowlisted model: `gpt-6-luna`. The [official model documentation](https://developers.openai.com/api/docs/models/gpt-6-luna)
was reviewed on 2026-10-31. Standard short-context input/output rates are $0.10/$0.50 per million tokens.
Accounting uses conservative rates of $0.25 input and $0.75 output per million tokens: long-context premiums
plus default 30-minute cache-write pricing, without cache-read discounts. Client-controlled cache options are
rejected, including longer paid cache lifetimes. These are accounting ceilings, not a quote for small requests.
Pricing review expires on 2026-11-06. Expired pricing fails closed.

GPT-6 Luna requires `reasoning_effort=none` for function calling in Chat Completions. The backend Agent enforces
this for that exact model even if an older environment configures `low`. Gateway rejects function-tool requests
with reasoning enabled before admission. Text-only requests may still use `none` or `low`. The endpoint and
strict JSON/tool contracts are unchanged. No live provider quality evaluation is performed by the offline tests.

No approximate character tokenizer is trusted. Reservation covers the configured 1,050,000-token context plus
bounded output. At the default 16,000 output-token limit, one backend AI attempt holds **$0.2745**. A smaller
remaining budget blocks admission. After valid usage arrives, the ledger retains only
`ceil((input * .25 + completion * .75) / 1,000,000, 10 decimal places)` and releases the rest.
`completion_tokens` already includes reasoning; never add reasoning tokens a second time.
Reported USD is a conservative estimate, not an invoice. Reconcile actual provider charges before expanding use.

Timeouts, network errors, oversized responses, missing/invalid usage and non-2xx upstream responses retain the
entire hold and stop spending. Failed settlement leaves a reservation pending. Crashes never refund holds or
release concurrency slots automatically. Close an abandoned reservation only while stopped and after its original
request/process is known to have ended; closing charges the full hold. Duplicate settlement cannot refund it.

Both `global` and `chat` daily/monthly money and call caps must permit each attempt. Day/month boundaries use
the database clock in `Asia/Ho_Chi_Minh`; requests belong to their admission period. Restarts and budget edits do
not reset usage. Database failures, missing initialization and policy mismatches block provider calls.
The stop switch blocks new admissions; previously admitted calls can still incur their reserved costs.

## Configuration and deployment

1. Copy `ANGI.AiGateway/.env.example` to `ANGI.AiGateway/.env`. Set the dedicated budget DB password and
   the chat/admin secrets. Leave `OPENAI_GATEWAY_PROVIDER_KEY` empty during offline setup. Never commit secrets.
2. From `angi-backend`, run:

   ```powershell
   docker compose --env-file ANGI.AiGateway/.env -f docker-compose.ai-gateway.yml up -d --build
   ```

   Migration initializes the separate `angi_ai_budget` database and persistent volume. It does not run EF
   migrations or touch the business `core` and `recommendation` schemas.
3. Configure only the backend environment/user-secrets:

   ```text
   Ai__BaseUrl=http://127.0.0.1:18080/v1/
   AiBudgetGateway__BaseUrl=http://127.0.0.1:18080/v1/
   Ai__ApiKey=<angi-chat-scoped-secret>
   Ai__Model=gpt-6-luna
   Agent__ReasoningEffort=none
   ```

   Both Agent and roadmap clients reject raw provider keys, route to the configured gateway, and disable redirects.
   Update any previous backend provider/model overrides when deploying. Reco is configured independently.
4. Host access is `http://127.0.0.1:18080/v1/`. A backend container sharing the compose network uses
   `http://ai-gateway:8080/v1/`; its own `localhost` cannot reach the gateway container.

Resource limits are under `Gateway` in `ANGI.AiGateway/appsettings.json`; class defaults and allowed ranges are
in `GatewaySettings.cs`. Environment variables such as `Gateway__RequestsPerMinute` override JSON when passed
to the process (add them to the compose service's `environment` if using Docker). Restart/rebuild to apply them.
Model, context and accounting rates are code constants. Money and daily/monthly call limits live in PostgreSQL
and are changed using the admin API without a restart.

Changing resource limits, pricing review date or accounting policy changes `PolicyHash`. Stop all instances,
settle pending reservations, apply the change consistently, then explicitly confirm the policy before resuming.
Switching models/pricing or upgrading from the former combined gateway follows the same procedure. Old model
requests are rejected; update backend model environment/user-secrets overrides along with the gateway.
Initialization preserves historical
ledger records and any legacy tables; historical reservations still count toward global controls. It does not
delete data or clear spending totals. Legacy vector credentials are no longer accepted, even if old environment
variables remain.

For running from source, set `Gateway__ConnectionString` to the separate budget database, then:

```powershell
dotnet run --project ANGI.AiGateway -- --migrate
dotnet run --project ANGI.AiGateway --urls http://127.0.0.1:18080
```

The gateway's local launch profile uses `Development` and port 18080, so it also loads its own .NET user-secrets.
Store `Gateway:ProviderApiKey`, `Gateway:ChatKey` and `Gateway:AdminKey` against the `ANGI.AiGateway` project.
Store the matching chat credential as `Ai:ApiKey` against `ANGI.WebApi`, with `Ai:Model=gpt-6-luna` and
`Agent:ReasoningEffort=none`. Docker deployments do not load these local user-secrets; use their environment
configuration instead. Reco continues to read its own environment/ignored `.env`, not .NET user-secrets.

## Operator API

All `/admin/*` endpoints require `Authorization: Bearer <angi-admin-secret>`. The chat credential cannot manage
budgets; the admin credential cannot call the AI endpoint. Never send gateway credentials to the provider.

| Operation | Method / path | Body |
| --- | --- | --- |
| Stop new spending | `POST /admin/control` | `{"stopped":true}` |
| Inspect usage and holds | `GET /admin/status` | none |
| Set limits without resetting usage | `PUT /admin/budgets/global` and `/admin/budgets/chat` | `{"dailyUsd":0,"monthlyUsd":0,"dailyCalls":0,"monthlyCalls":0}` |
| Quote without calling AI | `POST /v1/quote` with chat key | same body as Chat Completions |
| Resume after policy review | `POST /admin/control` | `{"stopped":false,"confirmPricingVersion":"<pricingVersion from status>"}` |
| Close an abandoned reservation at full hold | `POST /admin/reservations/{id}/close-unknown` | none; must be stopped |

Emergency stop, with the admin secret already loaded in the shell environment:

```powershell
./ANGI.AiGateway/Stop-Spending.ps1
```

Until live limits are explicitly chosen, leave budgets at zero. For an initial backend check, quote one selected
chat request first. Choose both global and chat money/call limits, configure the backend provider key only on the
gateway, and confirm pricing. An Agent turn can make several AI attempts; a one-call allowance may stop a turn
before it completes. Inspect usage, holds and actual provider billing before expanding limits. There is no
vector batch or reco retry queue to operate through this gateway.

## Verification

```powershell
dotnet test ANGI.AiGateway.Tests --configuration Release
# Set ANGI_GATEWAY_TEST_CONNECTION to an isolated *_test PostgreSQL database for ledger tests.
dotnet test ANGI.UnitTests --configuration Release
```

Tests cover chat/admin authorization, rejected legacy vector credentials and management routes, model/JSON
restrictions, admission and settlement, unknown usage, PostgreSQL concurrency, restart holds, global/chat limits,
monthly limits and policy consistency. No test calls a paid provider. No reco source or configuration is changed.
