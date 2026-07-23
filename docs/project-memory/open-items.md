# Open items

Only unresolved decisions, risks, or blockers belong here. The full M0-M7 delivery backlog remains authoritative in `docs/开发执行规格-v1.0.md`.

## Repository publication

- The authorized public GitHub repository `Lyon1984/PoolAI` and protected bootstrap `main` branch exist. Six strict checks are required for administrators and other actors, conversations must be resolved, and force pushes/deletion are disabled. PR #1 and PR #2 validated the protected delivery and security-remediation paths; PR #4 validated the first production-source path with exact 1/1 changed-line coverage. Subsequent changes must use pull requests rather than direct pushes to `main`.

## Later implementation risk

- Before implementing the public AuditLog query surface, resolve how the internal `auditor` actor fact is represented without adding a response enum value to the frozen OpenAPI v1 contract; use a deliberately versioned public contract or an explicitly approved compatible representation rather than silently widening `AuditActorType`.
- OpenAPI freezes a minimum 24-hour idempotency retention window, but the Operations-owned maximum retention and cleanup policy for completed records that contain encrypted TOTP setup/recovery responses is not yet frozen. Define it before introducing cleanup or envelope-key retirement so secret replay remains available for the promised window without retaining replayable secret material indefinitely.
- Before M4, [`NormalizedUpstreamResult`](../../src/Modules/PoolAI.Modules.Gateway.Abstractions/NormalizedUpstreamResult.cs) Token fields must move beyond `long` to the frozen lossless Token representation and enforce the OpenAI safe-integer output boundary; the current abstraction cannot represent abnormal 78-digit upstream evidence.

## Environment inputs still required later

- Secret Provider/KMS implementation and key identifiers
- Production SMTP endpoint, credentials, certificate trust policy, and delivery/alert thresholds
- Production ingress, TLS, CORS allowlist, trusted proxy, and observability destinations

Do not put values for these inputs in this file.
