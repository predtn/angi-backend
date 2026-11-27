# Deploy (self-hosted, Docker Compose)

Production runs on one Linux server with Docker Compose. The backend ships as the Docker image built from the root `Dockerfile`: it runs as a non-root user on port 8080 and contains `/app/efbundle`, a self-contained EF migration bundle. The full runbook for the three services (backend, frontend, reco) is kept by the team outside the repo; this page covers what the backend needs.

## Topology

```
Browser ─HTTPS─▶ Cloudflare ─Tunnel─▶ cloudflared (systemd on the host)
        ─▶ Caddy (reverse proxy, the only port published on the host)
        ─▶ angi-frontend:3000 (Next.js) ─▶ angi-backend:8080 ─┬─▶ angi-postgres:5432 (schema core)
                                                               └─▶ angi-reco:8000 (X-Api-Key)
```

- One Compose project, `angi`, holds `angi-postgres`, `angi-backend`, `angi-frontend` and `angi-reco`. Containers call each other by service name on the network `angi_default`.
- The backend publishes no host port and has no public hostname. Only the frontend is reachable from Caddy; browsers never call the API directly.
- No CD: GitHub Actions runs CI only, releases are manual (see [Update](#update)).

## Server layout

```
~/angi/
├── compose.yaml              postgres, backend, frontend
├── docker-compose.reco.yml   reco (see the reco repo, docs/deploy.md)
├── frontend.Dockerfile       see the frontend repo, docs/deploy.vi.md
├── .env                      Compose variables (database passwords, NEXT_PUBLIC_*, RECO_REPO_PATH)
├── backend.env               backend environment
├── frontend.env, reco.env
├── angi-backend/  angi-frontend/  angi-reco/   clones of main
```

Every env file holds secrets: `chmod 600`, never committed.

## Compose services (backend part)

```yaml
name: angi

services:
  angi-postgres:
    image: pgvector/pgvector:0.8.7-pg16
    restart: unless-stopped
    environment:
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: ${POSTGRES_SUPERUSER_PASSWORD}
      POSTGRES_DB: angi
      ANGI_BACKEND_DB_USER: angi_backend
      ANGI_BACKEND_DB_PASSWORD: ${ANGI_BACKEND_DB_PASSWORD}
      ANGI_RECO_DB_USER: angi_reco
      ANGI_RECO_DB_PASSWORD: ${ANGI_RECO_DB_PASSWORD}
      TZ: UTC
    volumes:
      - angi_pgdata:/var/lib/postgresql/data
      - ./angi-backend/docker/postgres/init:/docker-entrypoint-initdb.d:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d angi"]
      interval: 5s
      timeout: 5s
      retries: 10

  angi-backend:
    build: ./angi-backend
    image: angi-backend:local
    restart: unless-stopped
    env_file: backend.env
    depends_on:
      angi-postgres:
        condition: service_healthy

volumes:
  angi_pgdata:
```

`compose.yaml` also defines `angi-frontend` (frontend repo docs).

## Database, once

On the first start with an empty volume, Postgres runs [docker/postgres/init/01-roles-schemas.sh](../docker/postgres/init/01-roles-schemas.sh): it creates the roles `angi_backend` (owns schema `core`) and `angi_reco` (owns schema `recommendation`) and the extensions `citext`, `vector` and `pg_trgm` (the `angi_backend` role cannot create extensions; the trigram indexes of DISH-01 search need `pg_trgm`). The script runs only while the volume is empty; the passwords in `.env` are used only then. Changing one later means `ALTER ROLE` in Postgres and updating `.env` and `backend.env` (or `reco.env`).

A fresh database has no tag catalogue: the data step of migration `AddMealRoleCookingMethodIngredientTagTypes` runs only when the old tag `local_sf_main` exists. Load the catalogue before owners add dishes.

## Environment variables (`backend.env`)

Keys use `__` instead of `:`. The [external service keys](configuration.md) are listed there; the values below are the production ones.

| Variable | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` (the image default): JSON console logs, one object per line |
| `ConnectionStrings__Default` | `Host=angi-postgres;Port=5432;Database=angi;Username=angi_backend;Password=<ANGI_BACKEND_DB_PASSWORD>` |
| `Jwt__SecretKey` | At least 32 random characters (`openssl rand -hex 32`) |
| `Cors__AllowedOrigins__0` | The public URL, e.g. `https://<domain>` |
| `Frontend__BaseUrl` | The public URL (base of the links in emails), no trailing `/` |
| `TrustedClientIp__ProxyKey` | At least 32 random characters; the same value as `BACKEND_PROXY_KEY` on the frontend |
| `Recommendation__BaseUrl` | `http://angi-reco:8000` (service name on `angi_default`) |
| `Recommendation__ApiKey` | At least 32 characters; the same value as `RECO_API_KEY` of reco |
| `Ai__Model`, `Ai__ApiKey` | The model and key of the Agent and AI roadmaps (see [Agent](agent.md#provider-calls)) |
| `Agent__MaxOutputTokens`, `Agent__ReasoningEffort` | `16000`, `none` |
| `Cloudinary__CloudName`, `Cloudinary__ApiKey`, `Cloudinary__ApiSecret` | Images and verification documents |
| `Google__ClientId` | Same value as `NEXT_PUBLIC_GOOGLE_CLIENT_ID` on the frontend |
| `Brevo__ApiKey` | The v3 API key (`xkeysib-…`), not the SMTP key (`xsmtpsib-…`) |
| `Mapbox__AccessToken` | The secret token (`sk.*`) for the Directions API; not the frontend's public `pk.*` token |

Keys with a default in `appsettings.json` (`Brevo__SenderEmail`, Agent message limits, …) are not set. `ForwardedHeaders__Enabled` stays `false`: the API is reached only from the frontend container, never through a proxy, so `X-Forwarded-For` would only let a caller choose its IP.

### Real client IP

Every request reaches the API from the frontend's Next.js server, so without help all guests share the frontend container's IP and one anonymous budget (300 reads and 100 writes a minute). The chain that carries the browser IP:

1. Cloudflare puts the visitor IP in `CF-Connecting-IP`.
2. Caddy overwrites `X-Forwarded-For` and `X-Real-IP` with it (`header_up`, frontend docs).
3. The frontend sends it in `X-Angi-Client-Ip` with `X-Angi-Proxy-Key`.
4. The API uses that IP only when the key matches `TrustedClientIp__ProxyKey` (`TrustedClientIpMiddleware`). A wrong or missing key falls back to the connection IP, so a direct caller cannot choose its own IP.

Leave the key empty locally.

### Refresh tokens across frontend instances

AUTH-06: two requests whose access token expired together can both refresh with the same token (the self-hosted frontend runs one container, but parallel requests still race). A token rotated less than 30 seconds earlier therefore gets its own parallel session (`user_sessions.replaced_by_session_id` links a rotated session to its successor). A token reused after 30 seconds, or one revoked by logout, is still treated as stolen and ends every session of the user.

## Migrations

The API never migrates outside Development. Run the bundle from the freshly built image before starting it:

```bash
cd ~/angi
docker compose run --rm --entrypoint ./efbundle angi-backend   # ends with "Done."
```

Running it with no pending migration is harmless. EF logs a few Information lines in red; only the final `Done.` matters.

## Instances

One container. Rate limits, login-failure counts and the account-status cache live in memory, so a second instance would split them.

## Release order

When a release changes several services: reco first (its migration runs when its container starts), then the backend (`./efbundle`, then the API), then the frontend. Reco only adds tables and columns ahead of the backend, so an older backend keeps working against a newer reco.

## Update

```bash
cd ~/angi
git -C angi-backend pull
docker compose build angi-backend          # build one service at a time: the server has 8 GB RAM
docker compose run --rm --entrypoint ./efbundle angi-backend
docker compose up -d --force-recreate angi-backend
docker compose ps angi-backend             # IMAGE must read angi-backend:local, not sha256:…
```

A changed `backend.env` only needs `docker compose up -d --force-recreate angi-backend`. If `docker compose ps` shows an `sha256:…` image, the new image was built but the old container is still running: recreate it.

Before pulling, check what changed since the running commit:

```bash
git -C angi-backend diff --name-only <running-commit> origin/main | grep -E "Migrations/[0-9]|appsettings"
```

## Health and logs

The image has no `wget` or `curl`, so internal checks run from the frontend container:

```bash
docker compose exec angi-frontend wget -qO- http://angi-backend:8080/health   # {"success":true,…"status":"Healthy"}
docker compose logs --since 10m angi-backend | grep -E '"LogLevel":"(Warning|Error|Critical)"' | cut -c1-300
```

`/health` (OPS-03) returns 200 when PostgreSQL answers, 503 `SERVICE_UNAVAILABLE` otherwise; it is not rate limited and needs no token. Expected warnings: `Failed to determine the https port` (TLS ends at Cloudflare) and `DataProtection-Keys … not persisted` (the keys live in the container; login uses its own JWT, so this does not affect sessions).

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Sign-up returns 503; log has `Brevo rejected the email with status 401` | SMTP key instead of API key, a truncated key, or Brevo blocking the server's new IP | Use the `xkeysib-` key; add the server's public IP to Brevo's Authorized IPs; recreate the container |
| A new migration has no effect | `efbundle` not run, or run from the old image | Rebuild, run `efbundle`, look for `Applying migration '…'` and `Done.` |
| `Recommendation:BaseUrl or ApiKey is not set` | `Recommendation__*` missing from `backend.env` | Add both; the key equals reco's `RECO_API_KEY` |
| `Found orphan containers (angi-angi-reco-1)` | `docker compose` read only `compose.yaml` | Ignore it, or set `COMPOSE_FILE=compose.yaml:docker-compose.reco.yml` in `.env`. Never pass `--remove-orphans`: it deletes the reco container |

## Background jobs

Temporary account suspensions are expired by `AccountSuspensionExpiryWorker` once per minute. It invokes the scoped expiry use case in batches of 100; PostgreSQL `SKIP LOCKED` prevents overwriting concurrent sanctions. Each batch restores active status, clears the deadline, commits, and invalidates the account-status cache. Revoked sessions remain revoked.

## Check the image locally

```bash
docker build -t angi-backend .
docker run --rm -p 8080:8080 -e Jwt__SecretKey=<32+ chars> -e "ConnectionStrings__Default=Host=host.docker.internal;..." angi-backend
curl http://localhost:8080/health
```
