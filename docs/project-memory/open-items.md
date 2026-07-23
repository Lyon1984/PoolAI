# Open items

Only unresolved decisions, risks, or blockers belong here. The full M0-M7 delivery backlog remains authoritative in `docs/开发执行规格-v1.0.md`.

## Repository publication

- The authorized public GitHub repository `Lyon1984/PoolAI` and protected bootstrap `main` branch exist. Six strict checks are required for administrators and other actors, conversations must be resolved, and force pushes/deletion are disabled. PR #1 and PR #2 validated the protected delivery and security-remediation paths; PR #4 validated the first production-source path with exact 1/1 changed-line coverage. Subsequent changes must use pull requests rather than direct pushes to `main`.

## Later implementation risk

- Proposed ADR 0008、精确十一诊断 OpenAPI window 与 forward migration 0009 已形成仅本地候选，用于修正 API Key name/reason 在 OpenAPI、.NET 与已签 0008 之间的 Unicode scalar/whitespace/control 冲突。它们仍为 `Proposed`/`proposed`、approval evidence 为 null；在 `@Lyon1984` 明确批准前不得签署、合并为兼容豁免或执行远程 migration。执行前还必须只读预检既有 `api_keys.name/revoke_reason`，因为 0009 对不合规旧行会原子失败且不会自动 trim/改写。
- Before implementing the public AuditLog query surface, resolve how the internal `auditor` actor fact is represented without adding a response enum value to the frozen OpenAPI v1 contract; use a deliberately versioned public contract or an explicitly approved compatible representation rather than silently widening `AuditActorType`.
- OpenAPI freezes a minimum 24-hour idempotency retention window, but the Operations-owned maximum retention and cleanup policy for completed records that contain encrypted TOTP setup/recovery responses is not yet frozen. Define it before introducing cleanup or envelope-key retirement so secret replay remains available for the promised window without retaining replayable secret material indefinitely.
- Before M4, [`NormalizedUpstreamResult`](../../src/Modules/PoolAI.Modules.Gateway.Abstractions/NormalizedUpstreamResult.cs) Token fields must move beyond `long` to the frozen lossless Token representation and enforce the OpenAI safe-integer output boundary; the current abstraction cannot represent abnormal 78-digit upstream evidence.

## Environment inputs still required later

- Secret Provider/KMS implementation and key identifiers
- Production SMTP endpoint, credentials, certificate trust policy, and delivery/alert thresholds
- Production ingress, TLS, CORS allowlist, trusted proxy, and observability destinations

Do not put values for these inputs in this file.
