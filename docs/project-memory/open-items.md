# Open items

Only unresolved decisions, risks, or blockers belong here. The full M0-M7 delivery backlog remains authoritative in `docs/开发执行规格-v1.0.md`.

## M0 exit blockers

1. PR #1 passed all six protected-branch checks, including fail-closed coverage-integrity/forbidden-scope and CodeQL with zero results, but remains draft and unmerged. Its diff has no production source, so the first source-changing PR must still prove non-zero changed-line coverage.
2. The repository now validates all DEC/AC mappings and a complete 38-Epic task-system-neutral import preview. Importing that preview into the real task system, reading back real IDs/owners, completing DEC/database/OpenAPI sign-off, and recording the M0 exit review still require external project-management/review evidence.

## Repository publication

- The authorized public GitHub repository `Lyon1984/PoolAI` and protected bootstrap `main` branch exist. Six strict checks are required for administrators and other actors, conversations must be resolved, and force pushes/deletion are disabled. PR #1 remains draft pending review; subsequent changes must use pull requests rather than direct pushes to `main`.

## Later implementation risk

- Before M4, [`NormalizedUpstreamResult`](../../src/Modules/PoolAI.Modules.Gateway.Abstractions/NormalizedUpstreamResult.cs) Token fields must move beyond `long` to the frozen lossless Token representation and enforce the OpenAI safe-integer output boundary; the current abstraction cannot represent abnormal 78-digit upstream evidence.

## Environment inputs still required later

- Secret Provider/KMS implementation and key identifiers
- SMTP sender and delivery policy
- Production ingress, TLS, CORS allowlist, trusted proxy, and observability destinations

Do not put values for these inputs in this file.
