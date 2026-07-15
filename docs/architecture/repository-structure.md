# PoolAI repository structure

Status: M0 scaffold design

This document extends the frozen Solution structure in `docs/开发执行规格-v1.0.md` with repository-level frontend, build, deployment, tooling, operations, and Codex-support directories. Project references include the narrow shared PostgreSQL runtime accepted by [`ADR 0002`](adr/0002-introduce-shared-postgres-transaction-runtime.md); this does not change bounded contexts, table ownership, migration ownership, or contract authority.

## Target tree

```text
PoolAI/
├── AGENTS.md
├── README.md
├── PoolAI.sln                         # created in M0
├── global.json                        # exact .NET 10 SDK feature band
├── Directory.Build.props
├── Directory.Packages.props
├── NuGet.Config
├── .editorconfig
├── BannedSymbols.txt
├── .gitignore
├── .dockerignore
│
├── .codex/
│   └── README.md                      # native-memory boundary and Codex notes
│
├── docs/
│   ├── README.md                      # contract priority
│   ├── 系统重构方案-v1.0.md             # target state, workstreams, cutover, gates
│   ├── project-memory/                # curated, versioned project context
│   ├── architecture/
│   │   ├── design-pattern-baseline.md
│   │   ├── repository-structure.md
│   │   └── adr/
│   ├── contracts/                     # authoritative OpenAPI/errors/fixtures
│   ├── database/                      # authoritative SQL and DB semantics
│   ├── runtime/                       # authoritative Redis/runtime contracts
│   └── 开发执行规格-v1.0.md
│
├── src/
│   ├── PoolAI.Api/
│   ├── PoolAI.Worker/
│   ├── PoolAI.Migrator/
│   ├── PoolAI.Application.Orchestration/
│   ├── PoolAI.Contracts/
│   ├── PoolAI.BuildingBlocks/
│   ├── PoolAI.Database.Migrations/
│   ├── PoolAI.Infrastructure.Postgres/ # shared runtime mechanics only; no business SQL/schema
│   ├── Modules/
│   │   ├── PoolAI.Modules.Identity.Abstractions/
│   │   ├── PoolAI.Modules.Identity/
│   │   ├── PoolAI.Modules.SubscriptionAccess.Abstractions/
│   │   ├── PoolAI.Modules.SubscriptionAccess/
│   │   ├── PoolAI.Modules.GroupQuota.Abstractions/
│   │   ├── PoolAI.Modules.GroupQuota/
│   │   ├── PoolAI.Modules.Supply.Abstractions/
│   │   ├── PoolAI.Modules.Supply/
│   │   ├── PoolAI.Modules.Routing.Abstractions/
│   │   ├── PoolAI.Modules.Routing/
│   │   ├── PoolAI.Modules.Usage.Abstractions/
│   │   ├── PoolAI.Modules.Usage/
│   │   ├── PoolAI.Modules.Operations.Abstractions/
│   │   ├── PoolAI.Modules.Operations/
│   │   ├── PoolAI.Modules.Gateway.Abstractions/
│   │   └── PoolAI.Modules.Gateway/
│   └── Adapters/
│       └── PoolAI.Adapters.OpenAI/
│
├── frontend/
│   ├── package.json
│   ├── pnpm-lock.yaml
│   ├── vite.config.ts
│   ├── tsconfig.json
│   └── src/
│       ├── app/                        # bootstrap, providers, layouts
│       ├── api/                        # generated/handwritten transport boundary
│       ├── features/                   # feature slices by user journey
│       ├── router/
│       ├── stores/                     # cross-page Pinia state only
│       ├── shared/                     # UI, formatting, security-safe utilities
│       └── test/
│
├── tests/
│   ├── PoolAI.UnitTests/
│   ├── PoolAI.ArchitectureTests/
│   ├── PoolAI.ContractTests/
│   ├── PoolAI.IntegrationTests/
│   ├── PoolAI.EndToEndTests/
│   └── PoolAI.LoadTests/
│
├── eng/
│   ├── build/                          # deterministic build entrypoints
│   ├── test/                           # quality-gate orchestration
│   ├── ci/                             # CI-provider adapters
│   ├── release/                        # manifest/SBOM/signing orchestration
│   └── policies/                       # dependency/forbidden-scope checks
│
├── deploy/
│   ├── compose/                        # Api/Worker/Migrator/PostgreSQL/Redis
│   ├── docker/                         # one Dockerfile per executable Host
│   ├── config/examples/                # non-secret configuration examples
│   ├── postgres/                       # cluster-level role bootstrap only
│   └── observability/                  # dashboards and alert definitions
│
├── tools/
│   ├── contracts/                      # OpenAPI/error/fixture validation
│   ├── migrations/                     # checksum/manifest validation
│   ├── quality/                        # architecture and forbidden-scope scans
│   ├── mock-openai/                    # deterministic upstream test double
│   └── local-dev/                      # developer convenience tooling
│
├── ops/
│   ├── runbooks/                       # migrate, restore, incident, key rotation
│   ├── scripts/                        # reviewed post-deployment operations
│   └── poolai/                         # local environment metadata; no secrets
│
└── artifacts/                          # generated build/test/load output; ignored
```

Only directories with maintained content are created before M0. The actual Solution, project directories, package manifests, lock files, Dockerfiles, and deployment definitions must be generated together with their validation in M0; empty placeholder projects are not evidence of implementation.

## Module implementation layout

Each `PoolAI.Modules.<Context>` implementation project follows this internal layout:

```text
PoolAI.Modules.<Context>/
├── Domain/
│   ├── Aggregates/
│   ├── ValueObjects/
│   ├── Events/
│   ├── Policies/
│   └── Services/
├── Application/
│   ├── Commands/
│   ├── Queries/
│   ├── Ports/Inbound/
│   ├── Ports/Outbound/
│   └── EventHandlers/
├── Infrastructure/
│   ├── Persistence/
│   ├── Redis/
│   ├── Security/
│   ├── Messaging/
│   └── Workers/
├── Endpoints/
└── DependencyInjection.cs
```

Folders that do not apply to a Context are omitted. A common folder name is not permission to create a generic cross-module service or repository.

## Shared PostgreSQL runtime layout

`PoolAI.Infrastructure.Postgres` is a technical infrastructure project, not a module or bounded context. Its allowed shape is deliberately smaller than a module implementation:

```text
PoolAI.Infrastructure.Postgres/
├── Configuration/                     # runtime data-source/pool options and validation
├── Transactions/                      # Npgsql data source, UoW factory, UoW and non-committing context
├── AdvisoryLocks/                     # dedicated-connection session lock and lease
├── Diagnostics/                       # technical health/telemetry without credentials or business rules
└── DependencyInjection.cs             # explicit Host registration only
```

It must not contain a `Domain/`, `Application/`, `Endpoints/`, `Repositories/`, `Entities/`, `Migrations/`, or module-specific `Workers/` subtree. It owns no table, business query, idempotency/outbox/audit semantics, quota function wrapper, or generic SQL executor. Authoritative SQL stays under `docs/database/` and is consumed only by `PoolAI.Database.Migrations`; explicit business-table SQL stays in the owning module's `Infrastructure/` adapter.

Only module `Infrastructure` namespaces, technical infrastructure adapters, tests, and Api/Worker Composition Roots may use this project. `PoolAI.BuildingBlocks`, every `*.Abstractions`, Domain/Application/Endpoints, `PoolAI.Contracts`, and `PoolAI.Application.Orchestration` remain vendor-neutral and may not reference it or Npgsql. A multi-layer module implementation project may carry the project reference only because its Infrastructure namespace needs it; Architecture Tests enforce that no other namespace uses its types.

## Repository area boundaries

- `src/` contains production .NET code only. It never references `tests/`, `tools/`, or deployment implementations.
- `src/PoolAI.Infrastructure.Postgres/` contains only Host-local PostgreSQL runtime mechanics accepted by ADR 0002. Api and Worker construct separate data sources and transaction runtimes; Migrator remains on its independent schema migration path.
- `frontend/` is an independent Vue workspace and consumes the versioned HTTP/SSE contract; it does not import backend implementation types.
- `tests/` contains the six frozen suites. Specialized concurrency, security, migration, adapter, and fault tests are subfolders or traits inside those suites, not new parallel test projects.
- `eng/` defines how artifacts are built, verified, and released. It does not contain business behavior.
- `deploy/` defines how built artifacts run. It does not compile application code and contains no secrets.
- `tools/` contains developer/test utilities and never ships as a production dependency.
- `ops/` contains post-deployment procedures and local metadata. Destructive operations require explicit approval and verification.
- `artifacts/` is disposable generated output and must stay ignored.

## Single-source contract policy

- OpenAPI remains at `docs/contracts/openapi-v1.yaml`.
- Error definitions and golden fixtures remain under `docs/contracts/`.
- SQL remains under `docs/database/`; `PoolAI.Database.Migrations` may link or embed it and must verify checksums.
- Redis keys, Lua, TTLs, and failure semantics remain at `docs/runtime/redis-contract.md`.
- Generated C# or TypeScript transport types are outputs, not a second editable contract.

Moving or duplicating these sources requires an ADR and an atomic update to all references and validation tooling.

## Reference enforcement

The project reference DAG and Host loading matrix in `docs/开发执行规格-v1.0.md` are mandatory. `PoolAI.ArchitectureTests` must fail on undeclared references, cycles, cross-module implementation references, Host-to-Host references, forbidden commercial namespaces, layer violations, production use of `PoolAI.Infrastructure.Postgres`/Npgsql outside Infrastructure or Api/Worker Composition Roots, or business SQL/Repository/migration ownership appearing in the shared runtime. Test projects may reference it only to verify those boundaries and real-PostgreSQL behavior.
