# Open items

Only unresolved decisions, risks, or blockers belong here. The full M0-M7 delivery backlog remains authoritative in `docs/开发执行规格-v1.0.md`.

## Repository publication

- The authorized public GitHub repository `Lyon1984/PoolAI` and protected bootstrap `main` branch exist. Six strict checks are required for administrators and other actors, conversations must be resolved, and force pushes/deletion are disabled. PR #1 and PR #2 validated the protected delivery and security-remediation paths; PR #4 validated the first production-source path with exact 1/1 changed-line coverage. Subsequent changes must use pull requests rather than direct pushes to `main`.

## M1 Exit governance

- The M1 Exit readiness implementation and evidence are published and merged through [PR #65](https://github.com/Lyon1984/PoolAI/pull/65) as [`main@f5acbd73`](https://github.com/Lyon1984/PoolAI/commit/f5acbd7325e0736b8cbdfac5d5ba08325967b8f3); independent final-main [quality](https://github.com/Lyon1984/PoolAI/actions/runs/30077853566) and [security](https://github.com/Lyon1984/PoolAI/actions/runs/30077853531) push runs passed. The governance gate remains open because M1-E1/E2/E3 Issues [#10](https://github.com/Lyon1984/PoolAI/issues/10), [#11](https://github.com/Lyon1984/PoolAI/issues/11), and [#12](https://github.com/Lyon1984/PoolAI/issues/12) are closed without permanent completion-evidence comments; each comment requires explicit `@Lyon1984` confirmation before publication. M1 Exit itself requires a separate explicit confirmation after those comments, and the completed PR/CI/merge/final-main evidence does not substitute for that signature. No current approval authorizes M2/M3 work, remote migration, deployment, RC, GA, or production acceptance.

## Later implementation risk

- ADR 0008/精确十一诊断 OpenAPI window 与 forward migration 0009 已分别取得 `@Lyon1984` 的永久批准证据，且本地、PR 与最终 `main` 的真实 PostgreSQL 18 已验证空库 0001..0009、函数/constraint/ACL、Unicode 边界、M1-E5 原子重放及“先等行锁、再采样数据库时间”。任何另行获准的远程执行前仍必须只读预检既有 `api_keys.name/revoke_reason`，因为 0009 对不合规旧行会原子失败且不会自动 trim/改写；当前签核、测试和 CI 不授权远程 migration 或数据修复。
- Before implementing the public AuditLog query surface, resolve how the internal `auditor` actor fact is represented without adding a response enum value to the frozen OpenAPI v1 contract; use a deliberately versioned public contract or an explicitly approved compatible representation rather than silently widening `AuditActorType`.
- OpenAPI freezes a minimum 24-hour idempotency retention window, but the Operations-owned maximum retention and cleanup policy for completed records that contain encrypted TOTP setup/recovery responses is not yet frozen. Define it before introducing cleanup or envelope-key retirement so secret replay remains available for the promised window without retaining replayable secret material indefinitely.
- Before M4, [`NormalizedUpstreamResult`](../../src/Modules/PoolAI.Modules.Gateway.Abstractions/NormalizedUpstreamResult.cs) Token fields must move beyond `long` to the frozen lossless Token representation and enforce the OpenAI safe-integer output boundary; the current abstraction cannot represent abnormal 78-digit upstream evidence.

## Environment inputs still required later

- Secret Provider/KMS implementation and key identifiers
- Production SMTP endpoint, credentials, certificate trust policy, and delivery/alert thresholds
- Production ingress, TLS, CORS allowlist, trusted proxy, and observability destinations

Do not put values for these inputs in this file.
