#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

export CI="${CI:-true}"
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"
export DOTNET_NOLOGO="${DOTNET_NOLOGO:-1}"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE="${DOTNET_SKIP_FIRST_TIME_EXPERIENCE:-1}"
export DOTNET_MULTILEVEL_LOOKUP="${DOTNET_MULTILEVEL_LOOKUP:-0}"

if [[ -x "$repo_root/.tools/node/bin/node" ]]; then
  export PATH="$repo_root/.tools/node/bin:$PATH"
  export COREPACK_HOME="${COREPACK_HOME:-$repo_root/.tools/corepack}"
fi

if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
  export PATH="$repo_root/.tools/dotnet:$PATH"
  export DOTNET_ROOT="${DOTNET_ROOT:-$repo_root/.tools/dotnet}"
  export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$repo_root/.tools/dotnet-home}"
  export NUGET_PACKAGES="${NUGET_PACKAGES:-$repo_root/.nuget/packages}"
  export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$repo_root/.nuget/http-cache}"
fi

local_docker_socket="$repo_root/.tools/colima-home/poolai/docker.sock"
if [[ -S "$local_docker_socket" && -z "${DOCKER_HOST:-}" ]]; then
  export DOCKER_HOST="unix://$local_docker_socket"
  export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE="${TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE:-/var/run/docker.sock}"
fi

node eng/policies/validate-version-locks.mjs
node eng/test/verify-verified-download.mjs
node eng/test/verify-repository-file-safety.mjs
node eng/release/validate-release-manifest.mjs
node eng/policies/validate-traceability.mjs --structure-only
node eng/release/prepare-task-import.mjs --check
node eng/test/verify-task-import-safety.mjs
eng/policies/forbidden-scope.sh
eng/policies/coverage-integrity.sh
tools/local-dev/prepare-compose.sh
tools/local-dev/validate-compose.sh

rm -rf "$repo_root/frontend/coverage" "$repo_root/artifacts/coverage/dotnet"
mkdir -p "$repo_root/artifacts/coverage/dotnet"

pnpm --dir frontend install --frozen-lockfile
pnpm --dir frontend lint
pnpm --dir frontend typecheck
pnpm --dir frontend test:coverage
pnpm --dir frontend contracts:check
pnpm --dir frontend build

dotnet tool restore
dotnet restore PoolAI.sln --locked-mode
dotnet build PoolAI.sln --no-restore
node eng/policies/validate-traceability.mjs --compiled-tests
dotnet test PoolAI.sln \
  --no-build \
  --collect "XPlat Code Coverage" \
  --settings eng/test/coverage.runsettings \
  --results-directory artifacts/coverage/dotnet
node eng/test/verify-fail-skips.mjs
node eng/test/verify-coverage.mjs artifacts/coverage/dotnet
