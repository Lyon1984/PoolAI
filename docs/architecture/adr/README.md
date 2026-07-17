# Architecture decision records

Use ADRs only for decisions that change a frozen architectural boundary, such as bounded contexts, table ownership, cross-module direction, Host loading, Composition Roots, migration ownership, public contract compatibility, or deployment topology.

File naming:

```text
NNNN-short-kebab-title.md
```

Each ADR must contain status, date, context, decision, alternatives, consequences, migration/rollback impact, security impact, and the contract/test files updated by the decision. Accepted ADRs do not silently rewrite historical ADRs; superseding decisions link both directions.

Project memory links to ADRs but does not duplicate their decision text.

Accepted decisions:

- [`0001-separate-group-quota-from-supply-configuration.md`](0001-separate-group-quota-from-supply-configuration.md) — separate GroupQuota lifecycle/quota writes from Supply configuration writes.
- [`0002-introduce-shared-postgres-transaction-runtime.md`](0002-introduce-shared-postgres-transaction-runtime.md) — isolate the shared Npgsql data source, explicit Unit of Work context, and session advisory-lock runtime behind vendor-neutral ports.
- [`0003-approve-one-exact-pre-external-openapi-v1-reset.md`](0003-approve-one-exact-pre-external-openapi-v1-reset.md) — authorize one hash- and diagnostic-pinned M1-E1 OpenAPI v1 transition before external release evidence exists.

Proposed decisions pending explicit approval:

- [`0004-keep-subscription-template-catalog-admin-only.md`](0004-keep-subscription-template-catalog-admin-only.md) — align the RBAC matrix with the frozen Admin-only Template catalog and User self-read Subscription projections.
- [`0005-open-exact-v1-compatibility-window-for-admin-list-validation.md`](0005-open-exact-v1-compatibility-window-for-admin-list-validation.md) — register an exact, fail-closed `/v1` compatibility-window candidate for the three M1-E4 Admin list validation responses; it remains non-effective until explicit approval evidence exists.
- [`0006-register-group-subscription-lifecycle-fence.md`](0006-register-group-subscription-lifecycle-fence.md) — freeze the three exact cross-context PostgreSQL read/row-lock exception families; it remains non-effective until explicit architecture approval exists.
