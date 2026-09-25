#!/bin/bash
# Runs once, on first initialisation of the postgres data volume.
# Creates two least-privilege roles:
#   omnichannel_migrator - owns the database, runs EF Core migrations (DDL).
#   omnichannel_app      - used by API/Workers at runtime (DML only, no DDL).
set -euo pipefail

: "${OMNI_MIGRATOR_PASSWORD:?OMNI_MIGRATOR_PASSWORD is required}"
: "${OMNI_APP_PASSWORD:?OMNI_APP_PASSWORD is required}"
DB_NAME="${OMNI_DB_NAME:-omnichannel}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v migrator_pw="$OMNI_MIGRATOR_PASSWORD" \
  -v app_pw="$OMNI_APP_PASSWORD" \
  -v db_name="$DB_NAME" <<'EOSQL'
CREATE ROLE omnichannel_migrator LOGIN PASSWORD :'migrator_pw';
CREATE ROLE omnichannel_app LOGIN PASSWORD :'app_pw';
CREATE DATABASE :"db_name" OWNER omnichannel_migrator;
REVOKE ALL ON DATABASE :"db_name" FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE :"db_name" TO omnichannel_app;
EOSQL

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$DB_NAME" <<'EOSQL'
-- PostgreSQL 15+: public schema is owned by pg_database_owner (= omnichannel_migrator)
-- and PUBLIC no longer has CREATE on it. Be explicit anyway.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO omnichannel_app;

-- Every table/sequence the migrator creates is automatically usable (DML only) by the app role.
ALTER DEFAULT PRIVILEGES FOR ROLE omnichannel_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO omnichannel_app;
ALTER DEFAULT PRIVILEGES FOR ROLE omnichannel_migrator IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO omnichannel_app;
EOSQL
