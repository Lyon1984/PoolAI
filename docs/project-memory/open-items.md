# Open items

Only unresolved decisions, risks, or blockers belong here. The full M0-M7 delivery backlog remains authoritative in `docs/开发执行规格-v1.0.md`.

## M0 exit blockers

1. GitHub Issues #6–#43 provide the 38 unique Epic task IDs and read-back owner `@Lyon1984`; Issue #44 indexes the import, and its separate permanent DEC, database, and OpenAPI approval comments close those three baseline sign-off gates. The M0 exit approval remains pending and must be last.
2. M0 exit also requires the R1.1 release environment, reference hardware, and load-report archive location to be explicit and reviewable; those production inputs are not yet recorded as verified evidence.

## Repository publication

- The authorized public GitHub repository `Lyon1984/PoolAI` and protected bootstrap `main` branch exist. Six strict checks are required for administrators and other actors, conversations must be resolved, and force pushes/deletion are disabled. PR #1 and PR #2 validated the protected delivery and security-remediation paths; PR #4 validated the first production-source path with exact 1/1 changed-line coverage. Subsequent changes must use pull requests rather than direct pushes to `main`.

## Later implementation risk

- Before M4, [`NormalizedUpstreamResult`](../../src/Modules/PoolAI.Modules.Gateway.Abstractions/NormalizedUpstreamResult.cs) Token fields must move beyond `long` to the frozen lossless Token representation and enforce the OpenAI safe-integer output boundary; the current abstraction cannot represent abnormal 78-digit upstream evidence.

## Environment inputs still required later

- Secret Provider/KMS implementation and key identifiers
- SMTP sender and delivery policy
- Production ingress, TLS, CORS allowlist, trusted proxy, and observability destinations

Do not put values for these inputs in this file.
