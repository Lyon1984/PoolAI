# PoolAI

PoolAI is a non-commercial subscription access and shared Group Token pool platform. The target implementation is a .NET 10 modular monolith with independent Api, Worker, and Migrator Hosts, a Vue 3 frontend, PostgreSQL 18, and Redis.

The repository has passed its independent M0 Exit gate and is authorized to
begin M1 development. Product, API, database, runtime, and architecture
contracts are present together with the locked .NET/Vue workspace, executable
quality gate, real-container integration tests, and local seven-service Compose
topology. No M1 business Epic or production-ready Release 1 implementation is
claimed complete.

## Repository map

```text
PoolAI/
├── AGENTS.md                    # Codex and contributor rules
├── .codex/                      # Codex usage notes; no generated memories
├── docs/                        # Authoritative contracts, architecture, ADRs, project memory
├── src/                         # .NET Hosts, building blocks, modules, adapters
├── frontend/                    # Vue 3 application workspace
├── tests/                       # Six frozen .NET test suites
├── eng/                         # Build, CI, release, and policy automation
├── deploy/                      # Container and deployment definitions
├── tools/                       # Contract, migration, quality, and local-dev tooling
├── ops/                         # Post-deployment runbooks and local operational metadata
└── artifacts/                   # Generated outputs; ignored by version control
```

The target system, capability disposition, and delivery route are documented in [`docs/系统重构方案-v1.0.md`](docs/系统重构方案-v1.0.md). The detailed tree and dependency rules are in [`docs/architecture/repository-structure.md`](docs/architecture/repository-structure.md). Start with [`docs/README.md`](docs/README.md) for contract priority and [`docs/project-memory/README.md`](docs/project-memory/README.md) for the current project state.
