#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if [[ $# -eq 0 ]]; then
  echo "Usage: tools/local-dev/run-with-toolchain.sh <command> [args...]" >&2
  exit 64
fi

dotnet_root="${DOTNET_ROOT:-$repo_root/.tools/dotnet}"
dotnet_cli_home="${DOTNET_CLI_HOME:-$repo_root/.tools/dotnet-home}"
nuget_packages="${NUGET_PACKAGES:-$repo_root/.nuget/packages}"
nuget_http_cache="${NUGET_HTTP_CACHE_PATH:-$repo_root/.nuget/http-cache}"
corepack_home="${COREPACK_HOME:-$repo_root/.tools/corepack}"
colima_home="${COLIMA_HOME:-$repo_root/.tools/colima-home}"
colima_cache_home="${COLIMA_CACHE_HOME:-$repo_root/.tools/colima-cache}"
lima_home="${LIMA_HOME:-$colima_home/_lima}"
lima_templates_path="${LIMA_TEMPLATES_PATH:-$repo_root/.tools/containers/share/lima/templates}"
docker_config="${DOCKER_CONFIG:-$repo_root/.tools/docker/config}"
docker_host="${DOCKER_HOST:-unix://$colima_home/poolai/docker.sock}"
testcontainers_socket="${TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE:-/var/run/docker.sock}"
compose_project_name="${COMPOSE_PROJECT_NAME:-poolai-dev}"

exec env \
  PATH="$repo_root/.tools/dotnet:$repo_root/.tools/node/bin:$repo_root/.tools/containers/bin:$repo_root/.tools/docker/bin:$PATH" \
  DOTNET_ROOT="$dotnet_root" \
  DOTNET_CLI_HOME="$dotnet_cli_home" \
  DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}" \
  DOTNET_NOLOGO="${DOTNET_NOLOGO:-1}" \
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE="${DOTNET_SKIP_FIRST_TIME_EXPERIENCE:-1}" \
  DOTNET_MULTILEVEL_LOOKUP="${DOTNET_MULTILEVEL_LOOKUP:-0}" \
  NUGET_PACKAGES="$nuget_packages" \
  NUGET_HTTP_CACHE_PATH="$nuget_http_cache" \
  COREPACK_HOME="$corepack_home" \
  COLIMA_HOME="$colima_home" \
  COLIMA_CACHE_HOME="$colima_cache_home" \
  LIMA_HOME="$lima_home" \
  LIMA_TEMPLATES_PATH="$lima_templates_path" \
  DOCKER_CONFIG="$docker_config" \
  DOCKER_HOST="$docker_host" \
  TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE="$testcontainers_socket" \
  COMPOSE_PROJECT_NAME="$compose_project_name" \
  "$@"
