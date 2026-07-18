# ADR 0006: Freeze the cross-context database read/lock allowlist

- Status: **Proposed**
- Date: 2026-07-17
- Decider: PoolAI architecture owner (`@Lyon1984`) — pending explicit approval
- Relates to: M1-E4 Issue #13 and sign-off control Issue #44
- Approval evidence: **Pending explicit approval**

## Context

PoolAI normally integrates bounded contexts through `*.Abstractions`, published
facts, or events. Two database-level exception families were described in the
architecture baseline: quota admission and the Group activation guard. Repository
audit found two places where the executable database behavior—one in the signed
baseline and one in the unsigned M1-E4 candidate—was narrower and more complete
than that prose:

1. the signed quota contract also makes `poolai_quota_settle`,
   `poolai_quota_mark_dispatched`, and `poolai_quota_adjust_usage` lock only the
   selected Account/Channel `id + provider` identity before recording dispatch or
   usage facts; and
2. the M1-E4 candidate uses a bidirectional Group–Subscription lifecycle fence to
   linearize Group archive against Template/Subscription mutations.

The first behavior already exists in signed `0002_quota_functions.sql`; this ADR
does not rewrite that migration. The second exists in the unsigned M1-E4 forward
migration candidate. Leaving either outside the architecture allowlist would make
tests accept an inaccurate contract or force an implementation to hide existing
database coordination.

Application snapshots cannot replace either protocol. A snapshot cannot keep a
route identity or Group lifecycle row stable until the owning command commits, and
an Orchestrator cannot create an atomic predicate across contexts without gaining
the same database coordination capability. Conversely, treating every quota or
control-plane function as implicitly exempt would erase table ownership.

This ADR therefore proposes one complete registry containing exactly three
exception families. It does not change any table owner and does not authorize a
shared Unit of Work, generic database access, or cross-context writes.

## Proposed decision

The R1 cross-context database `SELECT`/row-lock allowlist is frozen to the three
families below. This proposal becomes effective only after `@Lyon1984` explicitly
approves it and this ADR is changed to `Accepted` with permanent approval evidence.

### Family A: GroupQuota canonical admission and route-identity validation

`poolai_quota_reserve` may perform the complete canonical admission read in its
short GroupQuota transaction. Its cross-context field allowlist is:

- Identity: `users(id,status,deleted_at,locked_until)`,
  `user_roles(user_id)`, and
  `api_keys(id,user_id,group_id,status,expires_at)`;
- SubscriptionAccess:
  `subscriptions(id,user_id,group_id,status,starts_at,expires_at)`; and
- Supply: `group_supply_configurations(group_id,channel_id)`,
  `group_accounts(group_id,account_id,is_enabled)`,
  `accounts(id,status,deleted_at,last_health_status,
  upstream_rate_limited_until,provider)`, and
  `channels(id,status,deleted_at,provider)`.

Its reads of `usage_requests`, `groups`, quota, period, reservation, event, and
Operations append tables remain GroupQuota-owned or separately registered
technical append behavior; they are not additional cross-context exceptions.

The reserve lock order starts at the Group quota row, then locks the immutable
request/Identity/Subscription/Group admission identity. A completed attempt replay
returns before current Supply locks. A new attempt locks Supply Configuration,
binding/Account, configured Channel, and then the quota period. It samples
`clock_timestamp()` only after those potentially blocking locks and re-evaluates
all status, expiry, lifecycle, health, cooldown, provider, and configured-Channel
predicates at that database time.

The following three GroupQuota functions are also part of Family A, but their
cross-context access is strictly smaller:

- `poolai_quota_settle`;
- `poolai_quota_mark_dispatched`; and
- `poolai_quota_adjust_usage`.

After locking their GroupQuota-owned quota/period/reservation facts, each may lock
only `accounts(id,provider)` and `channels(id,provider)` with `FOR SHARE`. This
revalidates that the frozen reservation route has one provider identity before a
dispatch or usage fact is committed. They cannot read Account/Channel lifecycle,
health, cooldown, configuration, binding, credential, Base URL, or model policy.
Any time-dependent write uses a fresh database time sampled after those final
route/provider locks.

No other quota function is covered. In particular, this family is not permission
for settlement paths to repeat current admission, reroute, or read credentials.

### Family B: Group activation guard

`poolai_validate_group_activation` alone may perform the database activation
guard. Its cross-context Supply allowlist is:

- `group_supply_configurations(group_id,channel_id)`;
- `group_accounts(group_id,account_id,is_enabled)`;
- `channels(id,status,deleted_at,model_rules,provider)`; and
- `accounts(id,status,deleted_at,last_health_status,
  upstream_rate_limited_until,provider)`.

The trigger may also read GroupQuota-owned current quota/period fields. It does not
lock or mutate Supply, expose a Supply representation, or turn point-in-time
readiness into a permanent invariant. The normal activation entry remains the
application Orchestrator and persists opaque readiness evidence.

### Family C: Group–Subscription lifecycle fence

Family C is one indivisible, bidirectional archive/provisioning protocol.

The GroupQuota-to-SubscriptionAccess direction is limited to
`poolai_group_update`, and only to its transition-to-archive branch. After taking
the quota and Group fences, it may read only:

- `subscriptions.group_id`;
- `subscriptions.status`; and
- `subscriptions.expires_at`.

That predicate may only decide whether archive is blocked by an effective
active/scheduled canonical Subscription. It cannot return or mutate a Subscription.

The SubscriptionAccess-to-GroupQuota direction is limited to:

- `poolai_subscription_template_create`;
- `poolai_subscription_template_update`;
- `poolai_subscription_template_retire`;
- `poolai_subscription_assign`; and
- `poolai_subscription_update`.

These functions may read only `groups.id` and `groups.status`. They may use the
Group row only to reject a missing, disabled, or archived Group and to acquire the
lifecycle fence. They cannot read quota, activation evidence, Group metadata,
timestamps, or versions.

Every participating path follows the global partial order
`Quota → Group → Template/Subscription`. Group archive locks quota and Group,
samples the database clock after those waits, and then inspects the Subscription
blocker while holding the Group fence. SubscriptionAccess mutations acquire the
Group fence before locking their own Template/Subscription row. An unlocked owner
row lookup may locate the stable `group_id`, but the owner row is re-read and
verified after locks are acquired. Template update may take the conflicting Group
lock needed to serialize same-Group unique-name changes; other paths use the
weakest lock preserving the fence. Time-dependent decisions use a fresh
`clock_timestamp()` after all potentially blocking row locks.

### Rules shared by all three families

Cross-table foreign keys remain migration-owned existence/integrity constraints;
they do not expand the business-read allowlist or transfer ownership.

Every exception is limited to short command-owned database work. It must not span
HTTP, SMTP, Redis, SSE, backoff, or other external waits. The allowlist authorizes
only the stated cross-context `SELECT` and row locks, never cross-context `INSERT`,
`UPDATE`, `DELETE`, `MERGE`, trigger-side mutation, or cascade ownership.

The registry must not become a generic SQL executor, dynamic SQL facility, shared
DbContext/Repository, `IQueryable`, callback, or a port exposing connection or
transaction capabilities. No additional function, table, field, lock direction,
or consumer is implicitly covered. Expansion requires a separate ADR and atomic
contract/test update.

## Alternatives considered

### Keep the prose limited to `poolai_quota_reserve`

Rejected. It would leave three signed route/provider locks outside the declared
allowlist and make a global Architecture Test either false or permanently red.
Registering their exact existing `id + provider` behavior corrects documentation;
it does not rewrite or broaden signed migration 0002.

### Replace database coordination with application ports

Rejected for these strict invariants. Ports remain the normal integration method,
but point-in-time snapshots cannot protect a selected route identity or Group
lifecycle until a downstream transaction commits.

### Put archive in `Application.Orchestration`

Rejected as a replacement for Family C. An Orchestrator can sequence snapshots and
one owner command but cannot atomically prevent an archive/provisioning race.

### Merge bounded contexts or add a reusable cross-context SQL service

Rejected. The contexts retain distinct owners and languages. A generic executor
would turn three enumerated protocols into an unbounded architecture bypass.

## Consequences

- The architecture contract accurately describes signed quota SQL and the M1-E4
  candidate without rewriting immutable migrations.
- Revoke/reserve, route/provider fact identity, activation readiness, and
  archive/provisioning have explicit linearization boundaries.
- Group, SubscriptionAccess, Identity, and Supply write ownership remains
  unchanged.
- Function-body allowlists, column grants, Architecture Tests, and real-role
  Integration Tests jointly enforce a boundary that the common R1 API database
  role cannot prove by itself.
- While this ADR remains `Proposed`, its candidate registry cannot be used to
  claim M1-E4 architecture sign-off or release readiness.

## Migration and rollback impact

- This ADR does not rewrite signed `0002_quota_functions.sql`; Family A registers
  its existing route/provider locks as an architecture contract without changing
  the migration bytes or checksum.
- Accepting this ADR approves only the three-family architecture registry. It does
  not approve, execute, or authorize merging unsigned
  `0007_group_subscription_m1_e4.sql`, its release-manifest entry, or the M1-E4
  pull request.
- If migration 0007 is later separately approved and executed, its bytes and
  checksum become immutable under the normal migration rules. Any correction must
  use a new forward migration rather than rewriting 0007.
- Expanding or shrinking the accepted registry, changing a function/table/field,
  or changing a lock direction requires a new ADR and an atomic contract/test
  update. It cannot be rolled back by restoring the former two-family prose.

## Acceptance evidence gate

Before this ADR may change to `Accepted`, all governance metadata and external
facts below must pass the fail-closed repository validator.

- The Proposed Decider remains exactly
  `PoolAI architecture owner (\`@Lyon1984\`) — pending explicit approval`.
  The Accepted Decider is exactly
  `PoolAI architecture owner (\`@Lyon1984\`)`; approximate, duplicated, or
  additional Decider values are invalid.
- Accepted metadata contains exactly one permanent Issue #44 comment link, one
  non-zero lowercase 40-character approved candidate SHA, and distinct
  `quality`/`security` Actions run links. The complete system plan may mention
  ADR 0006 on exactly two lines: the canonical section 6.2 scope bullet and one
  standalone normalized status bullet. No other section may assert a status or
  approval. When Accepted, the status bullet links the same comment target. The
  Proposed bullet is exactly
  `- ADR 0006 治理状态：**Proposed**（待 \`@Lyon1984\` 明确批准；不是 M1-E4 architecture sign-off 或 release-ready 证据）。`;
  the Accepted form replaces it with exactly
  `- ADR 0006 治理状态：**Accepted**（[Issue #44 永久批准评论](COMMENT_URL)）。`,
  where `COMMENT_URL` is the same exact target as the ADR metadata link.
  `docs/project-memory/current-state.md` contains the same unique canonical
  Proposed/Accepted status bullet and uses the same `COMMENT_URL` when Accepted;
  its other ADR 0006 references remain status-neutral.
- The comment is read through the GitHub API and must have the exact Issue #44
  `issue_url`, exact metadata `html_url`, and author login `Lyon1984`. Its body
  must equal the canonical UTF-8 template below after replacing each placeholder
  exactly once; no leading/trailing text or alternate Markdown label is allowed.
- Both Actions runs and both workflow definitions are read through the GitHub
  API. The workflows must be active and bind their exact IDs, names, and paths
  `.github/workflows/quality-gate.yml` and
  `.github/workflows/security-evidence.yml`. Each run must be a `pull_request`
  event, belong to `Lyon1984/PoolAI`, have `head_repository.full_name` equal to
  `Lyon1984/PoolAI`, bind the corresponding workflow ID/URL/path, have the exact
  name `quality-gate` or `security-evidence`, be `completed` with `success`, use
  the approved candidate as `head_sha`, and have the exact metadata `html_url`.
- On a pull request, the approved candidate must be an ancestor of
  `ADR0006_SIGNING_HEAD`. The candidate-to-signing-head diff contains exactly
  this ADR, `docs/系统重构方案-v1.0.md`, and
  `docs/project-memory/current-state.md`; `open-items.md` is not allowed. The
  candidate versions are exactly Proposed. Normalization replaces only each
  already-validated unique canonical system-plan/current-state status line with
  one fixed placeholder; it does not filter arbitrary matching prefixes. After
  ignoring only this ADR's governance metadata and those two status placeholders,
  all three files must be byte-identical to the signing head. This makes approval
  a governance-only follow-up to the tested candidate.
- The quality workflow invokes GitHub evidence through one exact, unconditional
  step immediately after Node setup and before dependency installation. The step
  cannot add `if`, `continue-on-error`, a custom shell, duplicate keys, or trailing
  properties; the verify job cannot add a job-level condition/continue-on-error,
  and workflow/job `defaults` cannot replace the fail-closed shell behavior.
- A `push` to `main` may skip ancestry and signing-only-diff checks so squash
  merges remain valid, but it never skips GitHub comment, author, body, candidate,
  workflow-name, run-result, run-head, URL, or repository verification.

The canonical comment body is the text between these markers, without an extra
leading or trailing newline:

<!-- ADR0006_APPROVAL_COMMENT_TEMPLATE_BEGIN -->
```text
APPROVED: ADR 0006 — Freeze the cross-context database read/lock allowlist

I, @Lyon1984, approve exactly these three exception families:
- Family A: GroupQuota canonical admission and route-identity validation, including only the registered Account/Channel id + provider locks on settle, mark-dispatched, and adjust-usage.
- Family B: the registered Group activation guard.
- Family C: the registered bidirectional Group–Subscription lifecycle fence.

I approve only the stated cross-context SELECT/row locks, the global Quota → Group → Template/Subscription lock order, post-wait PostgreSQL clock checks, and short transactions.

Excluded: every additional function, table, field, consumer, or lock direction; cross-context DML or trigger-side mutation; dynamic or generic SQL; shared DbContext, Repository, or Unit of Work; and HTTP, SMTP, Redis, SSE, backoff, or other external waits. This comment does not approve database migration 0007, its manifest/checksum or remote execution, PR merge, Issue closure, M1-E4 completion, RC/GA, deployment, or production release.

Approved candidate head: `<APPROVED_CANDIDATE_SHA>`
Approved quality-gate run: <APPROVED_QUALITY_RUN_URL>
Approved security-evidence run: <APPROVED_SECURITY_RUN_URL>
```
<!-- ADR0006_APPROVAL_COMMENT_TEMPLATE_END -->

A URL-shaped value without this complete readback is not approval evidence.

## Security impact

- The allowlist contains no password, TOTP secret, API-key digest, credential
  envelope, secret-provider value, private Base URL, personal quota, or commercial
  data.
- `SECURITY DEFINER` entry points retain fixed search paths and exact EXECUTE
  grants; runtime callers do not inherit the NOLOGIN owner.
- Static tests reject unknown functions/tables/fields, cross-context writes,
  dynamic SQL, and lock/time-order drift. Integration tests retain real-role and
  both-commit-order evidence.

## Contract and test updates required

This proposal is complete only when these assets agree:

- `docs/architecture/design-pattern-baseline.md`;
- `docs/开发执行规格-v1.0.md`;
- `docs/database/README.md`;
- `docs/系统重构方案-v1.0.md`;
- `eng/policies/validate-traceability.mjs` to keep the system-plan summary linked
  to all three registered families;
- Architecture Tests for the three exact families, functions, tables, fields,
  read-only boundary, lock order, post-wait database clock, and dynamic-SQL ban;
- PostgreSQL integration tests for reserve/revoke order, provider identity,
  activation readiness, both archive/mutation commit orders, same-Group Template
  cross-renames, direct-table permission denial, and cross-context writes failing.

Signed `0002_quota_functions.sql` is not rewritten by this documentation fix.
The M1-E4 migration and release manifest remain governed by their normal forward
migration and checksum rules.
