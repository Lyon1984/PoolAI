# Open items

Only unresolved decisions, risks, or blockers belong here. The full M0-M7 delivery backlog remains authoritative in `docs/开发执行规格-v1.0.md`.

## Repository publication

- The authorized public GitHub repository `Lyon1984/PoolAI` and protected bootstrap `main` branch exist. Six strict checks are required for administrators and other actors, conversations must be resolved, and force pushes/deletion are disabled. PR #1 and PR #2 validated the protected delivery and security-remediation paths; PR #4 validated the first production-source path with exact 1/1 changed-line coverage. Subsequent changes must use pull requests rather than direct pushes to `main`.

## Later implementation risk

- ADR 0009 remains `Proposed` and cannot be signed or merged as accepted until its own evidence gate closes: real PostgreSQL Supply persistence/replacement, deterministic concurrent CAS rewrap and crash/retry coverage, and database/log/trace no-plaintext proof are still missing. The current BCL-only Envelope runtime, Supply protector and local JSON restore tests are review candidates only.
- Production Account credential rewrap execution ownership is not yet fully frozen. Supply must own the selector and CAS writer; Operations may only trigger, fence, audit and observe it. Before implementation, choose the exact Host/job and least-privileged database identity. The current `poolai_worker` role can read `accounts.credential_envelope` for Supply health but cannot update it; granting a bounded write path requires a forward permission migration, database contract tests and a separate database sign-off. Manual SQL or an ad hoc production script is forbidden.
- AC-005 remains planned because its public create/query/export and log/trace evidence depends on the M2-E2 Account API even though the reviewed traceability map assigns the criterion to M2-E1. Do not silently remap the locked DEC/AC-to-Epic registry or add unapproved response fields: the signed OpenAPI Account response exposes `credential_prefix`, not envelope `kid` or key version.
- ADR 0008/精确十一诊断 OpenAPI window 与 forward migration 0009 已分别取得 `@Lyon1984` 的永久批准证据，且本地、PR 与最终 `main` 的真实 PostgreSQL 18 已验证空库 0001..0009、函数/constraint/ACL、Unicode 边界、M1-E5 原子重放及“先等行锁、再采样数据库时间”。任何另行获准的远程执行前仍必须只读预检既有 `api_keys.name/revoke_reason`，因为 0009 对不合规旧行会原子失败且不会自动 trim/改写；当前签核、测试和 CI 不授权远程 migration 或数据修复。
- Before implementing the public AuditLog query surface, resolve how the internal `auditor` actor fact is represented without adding a response enum value to the frozen OpenAPI v1 contract; use a deliberately versioned public contract or an explicitly approved compatible representation rather than silently widening `AuditActorType`.
- OpenAPI freezes a minimum 24-hour idempotency retention window, but the Operations-owned maximum retention and cleanup policy for completed records that contain encrypted TOTP setup/recovery responses is not yet frozen. Define it before introducing cleanup or envelope-key retirement so secret replay remains available for the promised window without retaining replayable secret material indefinitely.
- Before M4, [`NormalizedUpstreamResult`](../../src/Modules/PoolAI.Modules.Gateway.Abstractions/NormalizedUpstreamResult.cs) Token fields must move beyond `long` to the frozen lossless Token representation and enforce the OpenAI safe-integer output boundary; the current abstraction cannot represent abnormal 78-digit upstream evidence.

## Environment inputs still required later

- Secret Provider/KMS implementation and key identifiers
- Production SMTP endpoint, credentials, certificate trust policy, and delivery/alert thresholds
- Production ingress, TLS, CORS allowlist, trusted proxy, and observability destinations

Do not put values for these inputs in this file.
