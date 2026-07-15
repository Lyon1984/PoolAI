#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
compose_file="$repo_root/deploy/compose/compose.yaml"
toolchain="$repo_root/tools/local-dev/run-with-toolchain.sh"
publisher="$repo_root/eng/build/publish-hosts.sh"

"$repo_root/tools/local-dev/prepare-compose.sh"
export POOLAI_SECRET_DIR="$repo_root/.tools/compose/secrets"
"$repo_root/tools/local-dev/validate-compose.sh"

image_count=0
for image_value in \
    "${POOLAI_API_IMAGE:-}" \
    "${POOLAI_WORKER_IMAGE:-}" \
    "${POOLAI_MIGRATOR_IMAGE:-}"
do
    if [ -n "$image_value" ]; then
        image_count=$((image_count + 1))
    fi
done

if [ "$image_count" -ne 0 ] && [ "$image_count" -ne 3 ]; then
    echo "compose-up: set all three POOLAI_API_IMAGE, POOLAI_WORKER_IMAGE, and POOLAI_MIGRATOR_IMAGE values, or set none." >&2
    exit 1
fi

if [ "$image_count" -eq 3 ]; then
    "$toolchain" docker compose \
        --file "$compose_file" \
        up --no-build --wait --wait-timeout 180
    exec "$repo_root/tools/local-dev/verify-compose.sh"
fi

if [ ! -x "$publisher" ]; then
    echo "compose-up: missing executable Host publish entrypoint: $publisher" >&2
    exit 1
fi

"$publisher"

for artifact in \
    "$repo_root/artifacts/publish/PoolAI.Api/PoolAI.Api.dll" \
    "$repo_root/artifacts/publish/PoolAI.Worker/PoolAI.Worker.dll" \
    "$repo_root/artifacts/publish/PoolAI.Migrator/PoolAI.Migrator.dll"
do
    if [ ! -f "$artifact" ]; then
        echo "compose-up: missing pre-published artifact: $artifact" >&2
        echo "The repository Host publish entrypoint did not produce its deployment contract." >&2
        exit 1
    fi
done

"$toolchain" docker compose \
    --file "$compose_file" \
    up --build --wait --wait-timeout 180
exec "$repo_root/tools/local-dev/verify-compose.sh"
