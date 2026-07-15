# ADR 0002: Introduce a shared PostgreSQL transaction runtime

- Status: Accepted
- Date: 2026-07-15
- Deciders: PoolAI architecture baseline owners
- Relates to: D-026, DEC-032, DEC-037, DEC-038, and AC-037/040/041

## Context

The frozen architecture requires one explicit PostgreSQL Unit of Work for each Command. The business fact, command idempotency record, append-only audit record, and integration outbox message must share one physical connection and transaction, while the owning module and Operations keep separate implementation assemblies. Domain, Application, Endpoint, and `*.Abstractions` code must not see Npgsql, EF Core, `DbConnection`, or `DbTransaction` types, and a module implementation must not reference the Operations implementation merely to borrow its transaction.

`PoolAI.BuildingBlocks` already defines the vendor-neutral Unit of Work ports, but the original project graph does not assign ownership of the physical Npgsql connection/transaction carrier. Putting that carrier in a business module would transfer commit infrastructure to that module and force forbidden implementation-to-implementation references. Putting Npgsql in `PoolAI.BuildingBlocks` would leak a vendor dependency transitively into Domain/Application assemblies. Giving every module an independent connection would make the required atomic commit impossible.

The same missing technical boundary affects Worker session advisory locks. A lock must live on one dedicated Npgsql connection and be released when its lease is disposed or the connection dies, without turning Redis into a Worker leader and without giving a business module a general SQL execution service.

## Decision

### Shared technical project

1. Add `PoolAI.Infrastructure.Postgres` as a shared **technical infrastructure** project. It is not a bounded context, owns no table or business fact, and exposes no business language.
2. The project owns only the runtime PostgreSQL mechanics shared by infrastructure adapters:
   - construction and lifecycle of the Host-scoped `NpgsqlDataSource`;
   - creation of a short explicit Npgsql connection/transaction Unit of Work;
   - the concrete PostgreSQL Unit of Work context used to enlist parameterized Npgsql commands and, where needed, EF Core work in that same connection/transaction;
   - dedicated-connection PostgreSQL session advisory lock acquisition and lease disposal for Worker job ownership;
   - technical options validation, diagnostics, and health integration that do not contain business rules.
3. The project contains no business-table SQL, Repository, EF entity mapping, migration, idempotency/outbox/inbox/audit semantics, quota function wrapper, or module-specific Worker job. SQL that reads or writes business tables remains in the owning module's Infrastructure adapter. Authoritative DDL and migrations remain exclusively in `PoolAI.Database.Migrations` and `PoolAI.Migrator`.
4. The shared project does not expose a generic SQL executor, generic Repository, God DbContext, or service locator. It does not use `TransactionScope`, ambient transactions, `AsyncLocal` transaction lookup, a static/global connection registry, or a process-wide mutable “current transaction”.

### Vendor-neutral ports and explicit capability split

1. `PoolAI.BuildingBlocks` remains vendor-neutral. `IUnitOfWorkFactory.BeginAsync(...)` creates an `IUnitOfWork`; `IUnitOfWork` exposes an `IUnitOfWorkContext Context` and is the only capability that can `CommitAsync` and dispose the transaction.
2. `IUnitOfWork` no longer inherits or implements `IUnitOfWorkContext`. The returned `Context` is a distinct object that has no commit/dispose capability. Application handlers hold the `IUnitOfWork`, pass only `unitOfWork.Context` to repositories and transaction-bound Operations ports, and commit once.
3. `CommitAsync` is a one-shot terminal operation. Disposing an uncommitted Unit of Work rolls back; after commit/dispose, its context is no longer usable. A Unit of Work/context is not cached or shared across Commands.
4. Domain, Application, Endpoint, `*.Abstractions`, `PoolAI.Contracts`, and `PoolAI.Application.Orchestration` may reference only the vendor-neutral BuildingBlocks ports. They must not reference Npgsql, the concrete PostgreSQL context, or `PoolAI.Infrastructure.Postgres`.
5. A module's Infrastructure adapter may reference `PoolAI.Infrastructure.Postgres` and type-check the supplied vendor-neutral context for the concrete PostgreSQL transaction capability. A missing or incompatible capability fails closed before any write; the adapter must not open a fallback connection or transaction. The concrete context exposes the existing Npgsql connection/transaction only to Infrastructure code and never exposes commit.
6. This context check is a technical adapter boundary, not permission to share repositories or business SQL. Operations appenders may write only Operations-owned tables; the caller's module adapters may write only their owned tables or an already registered narrow database exception.

### Project references and Host composition

1. Only module `Infrastructure` namespaces, technical infrastructure adapters, tests, and the Api/Worker Composition Roots may reference `PoolAI.Infrastructure.Postgres`. A module project that contains multiple logical layers may carry the project reference, but Architecture Tests must prove that its Domain/Application/Endpoints namespaces never use the shared project or Npgsql types.
2. `PoolAI.Api` and `PoolAI.Worker` each register their own Host-scoped PostgreSQL runtime and close their object graph in their single `Program.cs` Composition Root. They do not share an `IServiceProvider`, data source instance, Unit of Work, or connection across processes.
3. `PoolAI.Worker` uses the shared dedicated-connection session advisory lock primitive for versioned per-job ownership. The lock does not replace job-level database idempotency, owner/generation fencing, inbox deduplication, or transactional claims.
4. `PoolAI.Migrator` remains the sole schema owner. It continues to load `PoolAI.Database.Migrations` and its migration-specific lock/history/checksum path; it does not register the runtime application Unit of Work or any business module. Introducing the shared runtime does not authorize Api or Worker to execute migrations.

## Alternatives considered

### Put Npgsql and the concrete transaction on `IUnitOfWorkContext`

Rejected. That would make a vendor type part of the BuildingBlocks/Application port and transitively expose infrastructure to every module abstraction and handler.

### Make `IUnitOfWork` itself the context passed to appenders

Rejected. Any recipient could downcast to the commit/dispose-capable object, so the type boundary would not enforce the single commit owner. A distinct context capability makes the prohibited action structurally unavailable.

### Place the Unit of Work in Operations or another business module

Rejected. Other modules would have to reference that module's implementation, Operations would become the de facto owner of every business transaction, and the project DAG would acquire a shared implementation hub.

### Give each module or appender its own connection and transaction

Rejected. Business facts, idempotency response, audit, and outbox could commit independently, violating AC-040 and producing irreconcilable partial success.

### Expose a generic SQL callback/executor from BuildingBlocks

Rejected. It hides vendor coupling behind a weak abstraction, makes table ownership and SQL allowlists difficult to enforce, and becomes a God data-access service. Infrastructure adapters must retain explicit parameterized SQL and typed persistence behavior.

### Use `TransactionScope`, ambient state, or a global connection registry

Rejected. Transaction ownership becomes implicit, async flow and pooling behavior become harder to reason about, accidental external-I/O transaction lifetimes become likely, and tests cannot reliably prove the one-Command/one-commit boundary.

## Consequences

- Cross-module Operations appenders can join the caller's exact PostgreSQL transaction without a module implementation reference and without exposing Npgsql to Application code.
- The Solution gains one narrowly scoped production project and explicit allowed references from module implementation projects and the Api/Worker Composition Roots.
- Infrastructure adapters must validate the concrete transaction capability and must never silently create an independent write path.
- The split between `IUnitOfWork` and `IUnitOfWorkContext` gives Architecture Tests a verifiable guarantee that appenders cannot commit or dispose the caller's transaction.
- The shared data source/pool remains Host-local. Api and Worker connection budgets and runtime roles are configured independently even when both use the same technical implementation.
- Session advisory locks have a reusable connection-lifetime implementation, while all business job semantics and fencing remain in their owning modules.
- The extra project reference is a deliberate exception only at the technical Infrastructure layer; it does not relax the rule that module implementations may consume another module only through `*.Abstractions`.

## Migration and rollback impact

This decision changes no database schema, data, OpenAPI contract, or deployment topology. It adds a project and changes the in-process transaction port shape before M0-E3 consumers are complete. No SQL migration or data backfill is required.

Rollback before dependent implementations are released consists of removing the project references and restoring the prior Unit of Work port shape in one code-and-contract change. After modules depend on the shared context, rollback requires coordinated replacement of every transaction-bound adapter; it must not be attempted by giving those adapters independent transactions. `PoolAI.Migrator` and existing database migration history are unaffected.

## Security impact

- Api and Worker create data sources with their own least-privilege runtime roles; the shared runtime neither elevates a role nor grants DDL.
- Connection strings and credentials remain secret-provider/configuration inputs and are never included in diagnostics, exceptions, audit, or project memory.
- The concrete context is unavailable to Domain/Application/Endpoint code, reducing accidental direct SQL and transaction-lifetime exposure.
- Context mismatch fails closed, preventing a “helpful” fallback connection from bypassing atomic audit, idempotency, outbox, or table-owner rules.
- Advisory locks use dedicated connections and versioned keys. Unexpected connection loss releases PostgreSQL session ownership; durable owner/generation or inbox/idempotency fences still prevent duplicate effects.
- The shared project accepts no arbitrary SQL delegate and owns no business queries, limiting it from becoming an injection or authorization bypass surface.

## Contract and test updates required

This decision is complete only when the following assets agree in one change:

- `docs/architecture/repository-structure.md`;
- `docs/architecture/design-pattern-baseline.md`;
- `docs/开发执行规格-v1.0.md`;
- Architecture Tests for the project-reference allowlist, namespace-layer use of Npgsql/shared runtime, vendor-neutral BuildingBlocks ports, distinct non-committing context, forbidden ambient/global/generic executor patterns, and Migrator isolation;
- Integration Tests on real PostgreSQL proving multi-adapter commit/rollback on one connection/transaction, one-shot commit and terminal context invalidation, no fallback transaction on context mismatch, commit-before/after fault behavior, Api/Worker registration boundaries, and dedicated advisory-lock release/takeover after dispose or connection loss.
