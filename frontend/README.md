# Frontend workspace

This is the independent Vue 3, TypeScript, Vite, Pinia, and Vue Router workspace. M0 intentionally contains only an accessible engineering scaffold; product routes and page states belong to M5 and are not preimplemented here.

Feature slices consume generated transport types under `src/api/generated/`, while the editable sources of truth remain under `../docs/contracts/`. Shared state is limited to genuinely cross-page concerns. Aggregate Token values remain canonical decimal strings and are formatted through `BigInt`, never through JavaScript `number`.

Run the locked toolchain from the repository root:

```bash
tools/local-dev/run-with-toolchain.sh pnpm --dir frontend install --frozen-lockfile
tools/local-dev/run-with-toolchain.sh pnpm --dir frontend contracts:check
tools/local-dev/run-with-toolchain.sh pnpm --dir frontend lint
tools/local-dev/run-with-toolchain.sh pnpm --dir frontend typecheck
tools/local-dev/run-with-toolchain.sh pnpm --dir frontend test
tools/local-dev/run-with-toolchain.sh pnpm --dir frontend build
```
