#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export POOLAI_SECRET_DIR="${POOLAI_SECRET_DIR:-$repo_root/.tools/compose/secrets}"

exec "$repo_root/tools/local-dev/run-with-toolchain.sh" node \
    "$repo_root/tools/local-dev/validate-compose.mjs"
