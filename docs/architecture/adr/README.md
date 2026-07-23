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
- [`0004-keep-subscription-template-catalog-admin-only.md`](0004-keep-subscription-template-catalog-admin-only.md) — keep the Subscription Template catalog on the Admin control plane while exposing only assigned snapshots through User self-read projections.
- [`0005-open-exact-v1-compatibility-window-for-admin-list-validation.md`](0005-open-exact-v1-compatibility-window-for-admin-list-validation.md) — authorize the exact base-, target-, and diagnostic-pinned M1-E4 OpenAPI v1 window for three Admin list validation responses.
- [`0006-register-group-subscription-lifecycle-fence.md`](0006-register-group-subscription-lifecycle-fence.md) — freeze the three exact cross-context PostgreSQL read/row-lock exception families. Its permanent [architecture approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5011030600) and independently approved [signing-gate lifecycle clarification](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5046436932) remain distinct from migration 0007 approval.
- [`0007-freeze-api-key-lifecycle-and-validation-contract.md`](0007-freeze-api-key-lifecycle-and-validation-contract.md) — freeze atomic same-Group API Key rotation, versioned pepper and canonical CIDR semantics, and authorize the exact M1-E5 validation compatibility window through the permanent [Issue #44 approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5053216021).
