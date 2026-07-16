# Contract tooling

This M0 tool validates the authoritative assets in `docs/contracts/` and the literal SQLSTATE `P0001` boundary in `docs/database/`. It never copies those inputs into an editable source tree.

Commands are run through the locked frontend Node workspace:

```bash
pnpm --dir frontend contracts:validate
pnpm --dir frontend contracts:test
pnpm --dir frontend contracts:generate
pnpm --dir frontend contracts:check
CONTRACT_DIFF_BASE=<40-hex-base-sha> tools/local-dev/run-with-toolchain.sh node tools/contracts/cli.mjs compatibility
```

`validate` checks OpenAPI 3.1 structure, local-only and closed references, the supported JSON Schema vocabulary, operation IDs, exact per-surface security schemes, error media types, AJV compilation, embedded examples, and the stable error catalog. `test` additionally validates JSON/SSE fixtures, Responses sequencing, Chat terminal behavior, and the exact 90-entry SQL `P0001` map, then proves key negative cases fail. `compatibility` requires an exact 40-hex Git base SHA, compares request and response schemas in their respective directions, freezes existing operations/components/error semantics and `contentMediaType`, and byte-freezes every `.sse` fixture already present at that base; new fixtures remain additive. `generate` deterministically writes TypeScript DTOs under `frontend/src/api/generated/` and C# DTOs under `src/PoolAI.Contracts/Generated/`. `all --check` performs every validation and fails if any generated output is stale. The quality workflow derives `CONTRACT_DIFF_BASE` from the exact pull-request base or preceding `main` commit and fails closed when that commit is unavailable.

The C# generator uses `JsonElement` only for explicitly approved mixed-shape properties and the `ChatMessage`, `LoginResult`, `ResponseCompletedOutputItem`, `ResponseInProgressOutputItem`, and `ResponseStreamEvent` root unions. New union locations, schema shapes, or keywords fail generation until the mapping is reviewed. Generated DTOs compile as part of `PoolAI.Contracts`; neither language keeps an independently edited copy of the authoritative OpenAPI or error catalog. Generated DTOs preserve transport fields and null/omission semantics, but the AJV and state-machine validators remain authoritative for cross-field, enum, const, and stream-sequencing rules.
