#!/bin/sh
set -eu

secret_value() {
    secret_path="$1"

    if [ ! -r "$secret_path" ] || [ ! -s "$secret_path" ]; then
        echo "required PostgreSQL bootstrap secret is missing or empty: $secret_path" >&2
        exit 1
    fi

    IFS= read -r value < "$secret_path"
    if [ -z "$value" ]; then
        echo "required PostgreSQL bootstrap secret is empty: $secret_path" >&2
        exit 1
    fi

    printf '%s' "$value"
}

# psql reads these into variables with \getenv. Passwords therefore do not
# appear in command arguments, the Compose environment, or tracked SQL.
export POOLAI_API_PASSWORD="$(secret_value /run/secrets/postgres-api-password)"
export POOLAI_WORKER_PASSWORD="$(secret_value /run/secrets/postgres-worker-password)"
export POOLAI_MIGRATOR_PASSWORD="$(secret_value /run/secrets/postgres-migrator-password)"
export POOLAI_DATABASE_NAME="${POSTGRES_DB:?POSTGRES_DB is required}"

echo "Provisioning PoolAI cluster roles for the local database."
psql \
    --set=ON_ERROR_STOP=1 \
    --username "${POSTGRES_USER:?POSTGRES_USER is required}" \
    --dbname "$POSTGRES_DB" \
    --file /opt/poolai/bootstrap/runtime-roles.sql

unset POOLAI_API_PASSWORD POOLAI_WORKER_PASSWORD POOLAI_MIGRATOR_PASSWORD
