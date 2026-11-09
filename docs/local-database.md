# Local database

PostgreSQL 16 + pgvector runs in Docker. One database `angi`, two schemas, two roles (see `.agents/project_architecture.md` §6):

| Role | Schema | Used by |
|---|---|---|
| `angi_backend` | `core` | This backend (EF Core migrations) |
| `angi_reco` | `recommendation` | Recommendation service (Alembic migrations) |

Each role can only create and read tables in its own schema. Extensions `citext` and `vector` are already installed in schema `public`.

## Connection details

| | Backend | Recommendation service |
|---|---|---|
| Host / Port | `localhost` / `5432` (`POSTGRES_PORT`) | same |
| Database | `angi` | `angi` |
| User / Password | `angi_backend` / `backend_dev` | `angi_reco` / `reco_dev` |
| Default schema | `core` | `recommendation` |

Superuser (only for troubleshooting): `postgres` / `postgres_dev`.

## Common commands

```bash
docker compose up -d --wait    # start and wait until healthy
docker compose stop            # stop, keep data
docker compose logs postgres   # view logs
docker compose down -v         # delete the container AND all local data
```

The init script `docker/postgres/init/01-roles-schemas.sh` runs **only when the data volume is empty**. After changing the script or the role settings in `.env`, run `docker compose down -v` and start again (all local data is lost).

## Migrations

Running the API in Development (`dotnet run --project ANGI.WebApi`, or F5 in the IDE) applies pending EF migrations before it starts, so after `git pull` you do not need `dotnet ef database update`. Other environments never migrate on startup; they run migrations as a deploy step ([deploy](deploy.md)).

Add a migration after changing an entity or configuration:

```bash
dotnet ef migrations add <Name> -p ANGI.Infrastructure -s ANGI.WebApi -o Persistences/Migrations
```

| What you see | Meaning |
|---|---|
| `fail: ... Failed executing DbCommand ... __EFMigrationsHistory` on the first run | Normal on an empty database: EF looks for its history table before creating it |
| `PendingModelChangesWarning: The model for context 'ANGIContext' has pending changes` | You changed an entity or configuration without a migration (command above) |
| `Failed to connect to 127.0.0.1:5432` | The database is not running: `docker compose up -d --wait` |

## Troubleshooting

| Problem | Fix |
|---|---|
| `port is already allocated` | Another PostgreSQL uses 5432. Set `POSTGRES_PORT=5433` in `.env` and use that port in the connection string. |
| Log shows `$'\r': command not found` | The script was checked out with CRLF. Run `rm docker/postgres/init/01-roles-schemas.sh && git checkout -- docker/postgres/init/01-roles-schemas.sh`, then `docker compose down -v` and start again. |
| `Copy .env.example to .env first` | The `.env` file is missing. |
| Startup migration fails with `permission denied to create extension "pg_trgm"` | Your data volume predates the extension in the init script (the service role cannot create it). Run once: `docker exec angi-postgres psql -U postgres -d angi -c "CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public"`, then start the API again. |
