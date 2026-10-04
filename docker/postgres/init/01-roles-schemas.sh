#!/bin/bash
# Runs once, only when the data volume is empty (first `docker compose up`).
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v db="$POSTGRES_DB" \
  -v backend_user="$ANGI_BACKEND_DB_USER" -v backend_password="$ANGI_BACKEND_DB_PASSWORD" \
  -v reco_user="$ANGI_RECO_DB_USER" -v reco_password="$ANGI_RECO_DB_PASSWORD" <<'EOSQL'
CREATE ROLE :"backend_user" LOGIN PASSWORD :'backend_password';
CREATE ROLE :"reco_user" LOGIN PASSWORD :'reco_password';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE :"db" TO :"backend_user", :"reco_user";
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- Each service owns exactly one schema (project_architecture.md §6)
CREATE SCHEMA core AUTHORIZATION :"backend_user";
CREATE SCHEMA recommendation AUTHORIZATION :"reco_user";

-- vector is not a trusted extension, so a non-superuser cannot create it.
-- Migrations keep CREATE EXTENSION IF NOT EXISTS, which becomes a no-op here.
CREATE EXTENSION IF NOT EXISTS citext WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA public;

ALTER ROLE :"backend_user" SET search_path = core, public;
ALTER ROLE :"reco_user" SET search_path = recommendation, public;
EOSQL
