#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
toolchain="$repo_root/tools/local-dev/run-with-toolchain.sh"
configuration="${POOLAI_BUILD_CONFIGURATION:-Release}"

cd "$repo_root"

"$toolchain" dotnet restore PoolAI.sln --locked-mode
"$toolchain" dotnet build PoolAI.sln \
  --configuration "$configuration" \
  --no-restore

mkdir -p \
  artifacts/publish/PoolAI.Api \
  artifacts/publish/PoolAI.Worker \
  artifacts/publish/PoolAI.Migrator

"$toolchain" dotnet publish src/PoolAI.Api/PoolAI.Api.csproj \
  --configuration "$configuration" \
  --no-build \
  --no-restore \
  --no-self-contained \
  --output artifacts/publish/PoolAI.Api \
  -p:UseAppHost=false

"$toolchain" dotnet publish src/PoolAI.Worker/PoolAI.Worker.csproj \
  --configuration "$configuration" \
  --no-build \
  --no-restore \
  --no-self-contained \
  --output artifacts/publish/PoolAI.Worker \
  -p:UseAppHost=false

"$toolchain" dotnet publish src/PoolAI.Migrator/PoolAI.Migrator.csproj \
  --configuration "$configuration" \
  --no-build \
  --no-restore \
  --no-self-contained \
  --output artifacts/publish/PoolAI.Migrator \
  -p:UseAppHost=false

echo "Published Api, Worker and Migrator artifacts in $configuration configuration."
