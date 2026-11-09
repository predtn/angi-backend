# Deploy (Render)

The API ships as the Docker image built from the root `Dockerfile` (Render: **Docker** runtime, root directory `angi-backend`). The image runs as a non-root user on port 8080 and contains `/app/efbundle`, a self-contained EF migration bundle.

## Render settings

| Setting | Value |
|---|---|
| Health Check Path | `/health` (OPS-03: 200 when PostgreSQL answers, 503 `SERVICE_UNAVAILABLE` otherwise; not rate limited, no token) |
| Pre-Deploy Command | `./efbundle` (applies pending migrations with `ConnectionStrings__Default`; the API itself never migrates outside Development) |
| Instances | 1. Rate limits, login-failure counts and the account-status cache live in memory, so a second instance would split them. |

## Database, once

As the database owner / superuser (the `angi_backend` role cannot create extensions; the trigram indexes of DISH-01 search need `pg_trgm`):

```sql
CREATE EXTENSION IF NOT EXISTS citext WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;
```

## Environment variables

On top of the [external service keys](configuration.md) (on Render, `:` becomes `__`: `Jwt__SecretKey`, `Recommendation__ApiKey`):

| Variable | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` (the image default): JSON console logs, one object per line, for Render's log search |
| `ConnectionStrings__Default` | The production database, role `angi_backend` |
| `Jwt__SecretKey` | At least 32 random characters |
| `Cors__AllowedOrigins__0` | The Vercel URL |
| `Frontend__BaseUrl` | The Vercel URL (base of the links in emails) |
| `ForwardedHeaders__Enabled` | `true` (Render's proxy sets `X-Forwarded-For`) |
| `TrustedClientIp__ProxyKey` | At least 32 random characters; the same value as `BACKEND_PROXY_KEY` on the frontend |
| `Ai__Model`, `Agent__MaxOutputTokens`, `Agent__ReasoningEffort` | `gpt-6-luna`, `16000`, `none` (see [Agent](agent.md#provider-calls)); update older overrides when deploying |

### Real client IP

`TrustedClientIp__ProxyKey` matters because the frontend calls the API from its Next.js server: without it every guest arrives with the Vercel server's IP and they all share one anonymous rate limit (100 requests / minute). With it, the frontend sends the browser IP in `X-Angi-Client-Ip` with `X-Angi-Proxy-Key`, and the API uses that IP only when the key matches (`TrustedClientIpMiddleware`). A wrong or missing key falls back to the connection IP, so a direct caller cannot choose its own IP. Leave it empty locally.

### Refresh tokens across frontend instances

AUTH-06: the frontend runs on several Vercel instances, so two requests whose access token expired together can both refresh with the same token. A token rotated less than 30 seconds earlier therefore gets its own parallel session (`user_sessions.replaced_by_session_id` links a rotated session to its successor). A token reused after 30 seconds, or one revoked by logout, is still treated as stolen and ends every session of the user.

## Release order

When a release changes several services: reco first (its migration, then the service), then the backend (`./efbundle`, then the API), then the frontend. See the reco README.

## Background jobs

Temporary account suspensions are expired by `AccountSuspensionExpiryWorker` once per minute. It invokes the scoped expiry use case in batches of 100; PostgreSQL `SKIP LOCKED` prevents overwriting concurrent sanctions. Each batch restores active status, clears the deadline, commits, and invalidates the account-status cache. Revoked sessions remain revoked.

## Check the image locally

```bash
docker build -t angi-backend .
docker run --rm -p 8080:8080 -e Jwt__SecretKey=<32+ chars> -e "ConnectionStrings__Default=Host=host.docker.internal;..." angi-backend
curl http://localhost:8080/health
```
