# Host artifact images

These Dockerfiles package already-published host artifacts. They deliberately do
not restore, build, test, or copy source code. The build context must contain:

- `artifacts/publish/PoolAI.Api/PoolAI.Api.dll`
- `artifacts/publish/PoolAI.Worker/PoolAI.Worker.dll`
- `artifacts/publish/PoolAI.Migrator/PoolAI.Migrator.dll`

The repository quality gate owns publication. Compose only packages those
outputs into separate non-root, read-only-compatible runtime images. A missing
artifact is therefore a clear precondition failure rather than an implicit
container-side build.

`tools/local-dev/compose-up.sh` calls the repository-owned
`eng/build/publish-hosts.sh` before packaging local images. Compilation remains
outside `deploy/`; supplying all three immutable `POOLAI_*_IMAGE` values skips
local publication and packaging.

Each Dockerfile has a sibling `*.Dockerfile.dockerignore` allowlist. Its build
context contains only that Host's pre-published artifact and Dockerfile assets;
repository source, `.tools` caches, and local Compose secrets are never sent to
the image builder.

Each host must load the KeyPerFile-compatible Secret Provider directory at
`/run/secrets`; filenames containing `__` map to configuration `:` separators.
Api must expose `/health/live` and `/health/ready`. Worker has no public HTTP
endpoint, and Migrator must terminate after applying or verifying migrations.
The `noble-chiseled-extra` runtime variants are intentional: the frozen
`Asia/Shanghai` configuration requires tzdata, and globalization-sensitive
validation requires ICU data to exist before Host startup.
