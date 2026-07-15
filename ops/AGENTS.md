# Operations-specific instructions

- Read `../AGENTS.md` and the relevant database/runtime contracts before changing operational assets.
- Keep secrets and environment-specific credentials out of tracked files and project memory.
- Do not copy business migrations into `ops/`; the Migrator and `docs/database/` remain authoritative.
- Prefer idempotent, non-interactive scripts with explicit target/environment checks.
- Never run destructive database, Docker, deployment, or credential actions without explicit user authorization and a verified rollback/recovery boundary.
- Record reusable procedures as runbooks, not as raw terminal transcripts.
