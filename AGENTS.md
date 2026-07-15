# PoolAI repository instructions

## Start here

Before making changes, read these files in order:

1. `docs/project-memory/README.md`
2. `docs/project-memory/project-context.md`
3. `docs/project-memory/current-state.md`
4. `docs/project-memory/open-items.md`
5. `docs/README.md`
6. `docs/系统重构方案-v1.0.md` when the task touches scope, sequencing, cutover, or acceptance
7. The task-relevant contract under `docs/contracts/`, `docs/database/`, `docs/runtime/`, or `docs/architecture/`

Project memory is a navigation and handoff layer. It never overrides the contract priority defined by `docs/README.md`.

## Product boundary

- Target backend: .NET 10, ASP.NET Core 10, EF Core 10, Npgsql 10, PostgreSQL 18, and Redis.
- Frontend: Vue 3, TypeScript, Vite, Pinia, and Vue Router; keep it contract-separated from the backend.
- Group is the only cumulative Token quota subject. Subscription grants access; it does not own a personal quota.
- Do not add Payment, Billing, Pricing, Balance, Refund, Promo, Redeem, Affiliate, commission, purchasable quota, or personal quota modules, schemas, routes, configuration, or placeholders.
- Release 1 is a greenfield .NET implementation. Do not reintroduce unrelated legacy migration or commercial features.

## Architecture rules

- Keep `PoolAI.Api`, `PoolAI.Worker`, and `PoolAI.Migrator` as separate executable Hosts with one Composition Root each. They must not reference one another.
- Follow the module and dependency DAG in `docs/开发执行规格-v1.0.md` and `docs/architecture/design-pattern-baseline.md`.
- Module implementations may reference another module only through its `*.Abstractions` project.
- `PoolAI.Application.Orchestration` owns no tables, entities, repositories, DbContext, endpoint, or cross-module transaction.
- Domain and Application code must not depend on EF Core, Npgsql, Redis, SMTP, ASP.NET transport types, or vendor SDKs.
- Endpoint code calls Application use cases only; it must not use DbContext, SQL, Redis, or Infrastructure implementations directly.
- One Command has one explicit PostgreSQL Unit of Work and one commit owner. Never hold a database transaction across HTTP, SMTP, Redis waits, SSE output, or backoff.
- Keep `docs/contracts/openapi-v1.yaml`, `docs/contracts/fixtures/`, and `docs/database/*.sql` as single sources of truth. Link or embed them; do not copy them into `src/`, `tests/`, or `deploy/`.

## Change workflow

1. Identify the governing contract before implementation.
2. If contracts disagree, stop and update the contract or add an ADR before coding around the conflict.
3. Keep changes inside the owning bounded context and update all coupled contract assets atomically.
4. Add or update tests proportional to the change: unit, architecture, contract, integration, end-to-end, or load.
5. Run the narrowest relevant checks first, then the repository quality gate.
6. Update project memory only when verified state, open items, or a reusable lesson actually changes.

When the M0 build scaffold exists, the default quality gate is expected to include:

```bash
dotnet restore PoolAI.sln --locked-mode
dotnet build PoolAI.sln --no-restore
dotnet test PoolAI.sln --no-build
pnpm --dir frontend install --frozen-lockfile
pnpm --dir frontend lint
pnpm --dir frontend test
pnpm --dir frontend build
```

Until those files exist, do not claim that build or test validation passed.

## Security and operational safety

- Never commit passwords, API keys, JWTs, database credentials, secret-provider values, production dumps, or private host material.
- Do not copy `ops/poolai/postgres-connection.env` into documentation, examples, logs, or project memory.
- Treat API keys and Account credentials as secrets even in tests; use deterministic fakes or secret injection.
- Destructive database, deployment, credential, or external-system operations require explicit user authorization and a scoped verification plan.
- Do not run automatic migrations from Api or Worker. `PoolAI.Migrator` is the only schema owner.

## Project memory maintenance

- `docs/project-memory/current-state.md` contains only evidence-backed current state and the next milestone.
- `docs/project-memory/open-items.md` contains unresolved questions, risks, and blockers; do not copy the M0-M7 backlog there.
- `docs/project-memory/lessons-learned.md` contains only reproduced, durable engineering lessons.
- New architecture decisions belong in `docs/architecture/adr/`; memory files link to them but do not duplicate decision text.
- Never store secrets, chat transcripts, temporary command output, personal data, or model reasoning in project memory.
- Keep memory concise. Remove resolved items instead of turning it into an append-only work log; version control preserves history after the repository is initialized.

## Scope-specific guidance

Nested `AGENTS.md` files under `src/`, `tests/`, `frontend/`, `docs/`, and `ops/` add narrower rules for those areas. The closest applicable guidance wins when it does not conflict with this root file or higher-priority system instructions.
