# ADR 0001: Separate GroupQuota from Supply configuration

- Status: Accepted
- Date: 2026-07-15
- Deciders: PoolAI architecture baseline owners
- Supersedes: the parts of D-018 and the frozen design baseline that placed `channel_id` on the Group aggregate or advanced Group version from Supply writes

## Context

The pre-implementation contracts assigned Group lifecycle and cumulative quota to GroupQuota, while assigning Channel, Account, and Group–Account bindings to Supply. The original public Group create/update request nevertheless combined all of those writes, `groups.channel_id` stored a Supply reference, and the `group_accounts` trigger advanced `groups.version`.

That shape cannot satisfy the frozen command boundary. A request that changes quota, Group status, Channel, and bindings has no single bounded-context Unit of Work or unambiguous commit owner. A Supply binding write that updates `groups.version` is also a cross-context write and makes a Group ETag change for facts GroupQuota does not own. Implementing around either problem would silently weaken the architecture contract.

Group activation still needs point-in-time Supply readiness. The existing architecture deliberately treats readiness as a prerequisite observed by an application orchestrator, not as a permanent cross-context invariant: a later Supply outage does not mutate or automatically disable the Group, and every new data-plane attempt must re-read canonical Supply state.

## Decision

### Ownership and persistence

1. GroupQuota exclusively owns `groups`, Group lifecycle, Group version, and the quota ledger. `groups` no longer contains `channel_id`, and no Supply command or trigger may update a Group row.
2. Supply exclusively owns a new `group_supply_configurations` aggregate/table. Its stable identity is `group_id`; it has its own `channel_id`, monotonic `version`, and timestamps. `channel_id` may be null so an existing configuration can be deliberately cleared or staged while unready.
3. `group_accounts` is a child of `group_supply_configurations`. Its `group_id` foreign key references the Supply configuration. Binding mutations advance only the Supply configuration version. A multi-row replacement may advance the version more than once; clients may depend only on monotonicity, not an exact increment count.
4. The foreign key from a Supply configuration to `groups(id)` is an existence/integrity dependency, not shared write ownership. Supply never updates GroupQuota tables.
5. GroupQuota records the latest successful activation evidence as nullable `activation_supply_readiness_token` and `activation_supply_observed_at`. The token is an opaque, versioned Supply value (initial format prefix `v1.`) covering the observed configuration, Channel, eligible Account/binding versions, lifecycle/health, and observation time. GroupQuota stores and republishes it for auditability but never parses it or treats it as a lease or capability. An active Group must have both evidence fields; later Supply changes do not rewrite them or the Group version.

### Commands and HTTP resources

1. Group create/update commands contain only GroupQuota-owned data. Creating a Group may initialize its quota in the same GroupQuota Unit of Work. Group representations and mutations do not contain `channel_id` or `account_bindings`.
2. Supply configuration is an independently versioned resource at `/api/v1/admin/groups/{groupId}/supply-configuration`:
   - `GET` is available to Admin, Operator, and Auditor;
   - `POST` creates the resource and requires Admin plus `Idempotency-Key`;
   - `PATCH` changes or clears Channel/bindings and requires Admin plus `Idempotency-Key`, the Supply configuration `If-Match`, and a non-empty reason;
   - there is no delete operation in R1.
3. Group and Supply configuration use independent strong ETags. A Group `If-Match` can never authorize a Supply mutation, and a Supply mutation never invalidates the Group ETag.
4. Account and Channel provider are explicit, immutable creation-time Supply fields. `provider` is `openai` or `openai_compatible`; the existing `platform: openai` denotes the R1 inbound protocol family and is not an upstream provider selector.

### Activation and runtime admission

1. `GroupActivationOrchestrator` reads the Group/quota snapshot, obtains a versioned `IGroupSupplyReadiness` result, and then calls one GroupQuota activation command with the expected Group version and opaque readiness evidence. The final mutation, idempotency record, audit record, and outbox append commit in one GroupQuota Unit of Work. The orchestrator owns no data and starts no transaction.
2. The registered database activation guard remains a narrow, read-only defense against direct invalid activation. It reads `group_supply_configurations`, Channel, binding, Account, and quota readiness, but writes only the Group row being activated.
3. `poolai_quota_reserve` continues to use the registered read-only cross-context admission exception. It locks/reads the Supply configuration and canonical binding/Channel/Account state, verifies the requested Channel is the configured Channel, and writes only GroupQuota/Operations-owned facts.
4. Supply invalidation after activation does not roll back or silently disable the Group. Every new request and failover attempt re-reads the current Supply configuration and fails closed when it is no longer schedulable.

## Alternatives considered

### Keep one composite Group command and use a cross-context transaction

Rejected. It creates a shared Unit of Work and commit owner across GroupQuota and Supply, contradicts the frozen dependency and transaction rules, and makes partial-failure behavior impossible to express cleanly.

### Move Group and quota ownership into Supply

Rejected. Group is the only cumulative Token quota subject and its lifecycle is coupled to quota admission, reset, reservation, and settlement. Moving it would collapse two distinct domain languages into Supply.

### Keep `groups.channel_id` as a read-only foreign reference

Rejected. Channel selection is a mutable Supply policy. Keeping the field on Group preserves split ownership, forces Supply changes to coordinate with Group version, and invites runtime callers to bypass the Supply snapshot.

### Store readiness evidence only in logs or audit payloads

Rejected. Activation needs durable, queryable evidence that survives log retention and can be emitted consistently in audit/outbox records. Opaque evidence columns preserve that proof without transferring Supply semantics to GroupQuota.

### Automatically disable a Group whenever Supply becomes unready

Rejected. That is an implicit cross-context write with race-prone availability semantics. R1 intentionally uses point-in-time activation readiness plus per-attempt canonical admission.

## Consequences

- Every write has one bounded-context owner and one PostgreSQL Unit of Work.
- Group and Supply optimistic concurrency are independent and meaningful.
- Control-plane clients perform separate GroupQuota and Supply requests; there is no atomic "create everything" operation.
- A configuration may exist in an unready staged state. Activation and runtime admission remain fail-closed.
- Supply configuration replacement needs deterministic binding validation and idempotency tests.
- Runtime database permissions and architecture tests must prevent Supply from updating `groups` and GroupQuota from updating Supply tables.
- The single R1 `poolai_api` database role cannot by itself prove in-process module ownership; repository/DbContext boundaries and architecture tests remain mandatory until roles are split by module in a future ADR.

## Migration and rollback impact

The repository has not yet executed the baseline migration in a signed-off environment, so `0001/0002/0003` are updated in place before M0 implementation. No production data migration is required.

If an unsigned development database already exists, it must be recreated from the updated baseline. After the baseline is signed off, this decision may only be changed by a forward migration and a superseding ADR. Rollback after public API use would be breaking because it would merge independent resources and ETags; rollback therefore means restoring the previous contract only before first external use, not copying Supply fields back into Group at runtime.

## Security impact

- The readiness token contains no credential, Base URL, secret-provider value, or private Account material; it is an opaque digest/evidence identifier.
- Supply responses never return credential ciphertext or decrypted secret material.
- All Supply writes require Admin authorization, idempotency, audit, and explicit optimistic concurrency on update.
- The activation guard and reserve function retain strict column allowlists and cannot read credential envelope columns.
- Cross-context foreign keys use restrictive delete behavior; no cascade may erase historical reservations, attempts, or bindings.

## Contract and test updates required

This decision is complete only when the following assets agree in one change:

- `docs/contracts/openapi-v1.yaml` and coupled fixtures;
- `docs/contracts/error-catalog.md` for Supply validation, RBAC, idempotency, and concurrency behavior;
- `docs/database/0001_baseline.sql`, `0002_quota_functions.sql`, `0003_runtime_permissions.sql`, and `docs/database/README.md`;
- `docs/architecture/design-pattern-baseline.md`;
- `docs/runtime/redis-contract.md` where canonical Supply reads are named;
- `docs/开发执行规格-v1.0.md` and `docs/系统重构方案-v1.0.md` where scope, sequencing, and acceptance mention Group supply setup;
- architecture tests for project references, write ownership, and forbidden cross-context data access;
- contract/integration tests for independent ETags, idempotent create/update, activation races/evidence, database permissions, per-attempt Supply recheck, provider mismatch, invalid/empty bindings, and active-Group behavior after Supply invalidation.
