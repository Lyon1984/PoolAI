# Current state

Last verified: 2026-07-16

## Verified present

- Target system, capability disposition, refactoring workstreams, and delivery gates: [`../系统重构方案-v1.0.md`](../系统重构方案-v1.0.md)
- Release contract index and frozen decisions: [`../README.md`](../README.md)
- Locked .NET 10 SDK, NuGet, Node, pnpm, frontend packages, and reviewed container digests under `global.json`, `Directory.Packages.props`, lockfiles, and [`../../eng/versions.json`](../../eng/versions.json)
- [`../../PoolAI.sln`](../../PoolAI.sln) with 25 production projects, six test projects, separate Api/Worker/Migrator Hosts, and architecture-enforced project boundaries
- Vue 3/Vite/Pinia/Router M0 application scaffold and its lint, typecheck, unit-test, contract, and production-build commands
- OpenAPI, error catalog, fixtures, deterministic contract generators, and validators under `docs/contracts/` and `tools/contracts/`
- PostgreSQL 18 baseline, quota functions, permissions, one-shot Migrator, checksum/advisory-lock migration runner, and real-container role/migration tests
- Canonical [`../release-manifest-v1.json`](../release-manifest-v1.json), build-time source/hash validation, PostgreSQL schema-history compatibility checks, Redis server/key-schema/TIME/script checks, Api readiness integration, and Worker startup fail-closed gating
- Accepted [`../architecture/adr/0002-introduce-shared-postgres-transaction-runtime.md`](../architecture/adr/0002-introduce-shared-postgres-transaction-runtime.md), Host-local PostgreSQL data sources, explicit one-shot Unit of Work/context, and shared dedicated-session advisory-lock mechanics under [`../../src/PoolAI.Infrastructure.Postgres/`](../../src/PoolAI.Infrastructure.Postgres/)
- Production command idempotency, audit/outbox append, outbox/inbox delivery fencing, email outbox delivery fencing, and Usage checkpoint adapters under the owning module Infrastructure layers
- Redis 8.8 ACL/time/script-load integration coverage and the versioned coordination contract under `docs/runtime/`
- Seven-service local Compose topology with an isolated runtime network, loopback-only published development endpoints, PostgreSQL/Redis/mock health gates, one-shot Migrator dependency gate, and live SNTP readiness controls
- Executable quality/security workflows and local quality gate under [`.github/workflows/`](../../.github/workflows/), [`../../eng/test/quality-gate.sh`](../../eng/test/quality-gate.sh), and `eng/security/`, including coverage ratchets, browser smoke, dynamic Compose evidence, secret/dependency/license/SBOM/image scanning, and non-root image checks
- Machine-validated Release 1 traceability under [`../traceability/`](../traceability/): all 42 DEC and 45 AC IDs have a reviewed primary WS/direct-Epic mapping, governing contracts, and either strict local test/quality-gate evidence or a unique named planned test; external task, owner, sign-off, target-CI, and exit-review fields remain explicitly pending
- Deterministic task-system import preparation covers all 38 M0–M7 Epics with primary/supporting WS, dependencies, deliverables, DoD, related DEC/AC/contracts/tests, and explicitly null external task IDs/owners while the real task-system gate remains pending

The former Sub2API feature-analysis document has been converted into the refactoring plan above and removed, so it cannot compete as a second source of truth.

## Latest verification

- The repository quality gate passed locked restore, 31-project build with zero warnings/errors, release/contract checks, frontend lint/typecheck/build and 8 tests, plus 203 .NET tests with zero failures or skips. All six xUnit projects enforce `failSkips`, and a dynamic-skip negative probe proves that a skipped test fails the gate.
- The .NET total is Unit 151, Architecture 15, Contract 4, Integration 22, End-to-End 10, and Load 1.
- Fresh coverage evidence includes all 25 production assemblies: overall line coverage is 79.55%, frontend line coverage is 92.86%, and Domain/Application line and branch coverage are both 100%. Quota, authentication, and secret-handling scopes match their count-backed no-decline baselines; pull-request changed-line coverage remains unevaluated until a non-zero Git base exists.
- Chromium Playwright smoke passed reload, DOM/status, failed-request, console/page-error, and Vite-overlay checks. The pinned Gitleaks local white-list scan reported no leaks without mounting the private PostgreSQL connection env; Trivy reported no High/Critical source dependency findings and verified license inventory for 284 npm and 657 NuGet packages; all three local application images run as `1654:1654`.
- Real PostgreSQL 18/Redis Testcontainers verification covers AC-039 dispatch-fence crash compensation, AC-040 one-transaction commit faults and idempotent replay, AC-041 outbox/inbox poison/replay fencing, email delivery takeover, dedicated Worker advisory-lock disconnect takeover, exact release-manifest acceptance by both runtime roles, schema checksum-drift rejection, and registered Redis script reload after a controlled cache flush.
- Dynamic local Compose verification passed after applying all three migrations through the real NOINHERIT/SET-only Migrator role; Api and Worker passed the shared PostgreSQL/Redis compatibility gates only after Migrator success, Worker remained `running` with zero restarts, and NTP readiness followed `200 -> 503 -> 503 -> 503 -> 200` while liveness remained healthy.
- The traceability policy passed with 42 decisions (4 `implemented-local`, 14 `partial`, 24 `planned`) and 45 acceptance criteria (2 `implemented-local`, 11 `partial`, 32 `planned`). It resolves evidence through compiled xUnit discovery or exact quality-gate commands, fails on skipped tests, validates the authoritative ID-to-WS applicability matrix, locks the reviewed ID/WS/Epic map, and rejects placeholder-form external evidence once an external gate is marked verified.
- The task-import preflight passed for all 38 M0–M7 Epics: 32 have direct R1.1 DEC/AC associations, while M3-E5, M5-E6, and M7-E1–E4 are explicitly locked as having no direct R1.1 association. Generated JSON/CSV remain previews and contain no external IDs or owners.

## Not yet implemented or verified

- The authorized private GitHub repository and initial `main` publication now exist, but target workflow and repository-policy evidence remains pending: first-PR changed-line coverage, CodeQL entitlement/run, full-history Gitleaks, browser matrix, SBOM/image scans, CI-hosted dynamic Compose, required checks, and branch protection still require target-system readback
- Project-management evidence outside the repository: real task IDs and owners for each DEC/AC, DEC sign-off references, database/OpenAPI review references, and the M0 exit approval
- M1 and later business workflows, including concrete command handlers, SMTP sender loops, and milestone-owned Admin replay surfaces; M0-E3 supplies their verified persistence and fencing baseline

## Current milestone

M0 remains active. The M0-E3 transaction/idempotency/messaging baseline, M0-E4 local runtime compatibility path, repository-owned PR/release gates, repository-side DEC/AC traceability, a complete 38-Epic task-import preview, and the authorized private GitHub `main` bootstrap are implemented. The next items require external evidence: read back the target workflow runs, exercise first-PR changed-line coverage, configure required checks/branch protection, import the preview into the real task system and read back IDs/owners, complete DEC/database/OpenAPI sign-off, and conduct the M0 exit review. M1 feature implementation must wait for M0 exit sign-off.
