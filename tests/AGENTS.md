# Test-specific instructions

- Read `../AGENTS.md` and the test/acceptance sections of `../docs/开发执行规格-v1.0.md`.
- Tests must prove contract and architecture behavior, not mirror implementation details.
- Use real PostgreSQL roles and Redis for integration semantics that depend on locks, transactions, permissions, Lua, clocks, or failure behavior.
- Reuse canonical fixtures from `docs/contracts/fixtures/`; do not copy and edit them under tests.
- Every regression test must fail for the pre-fix behavior and identify the governing contract.
- Never weaken assertions or mark tests skipped merely to make a quality gate pass.
