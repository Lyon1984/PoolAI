# Frontend-specific instructions

- Read `../AGENTS.md`, the frontend section of `../docs/开发执行规格-v1.0.md`, and the canonical OpenAPI/error fixtures before changing UI behavior.
- Keep backend transport behind `src/api/`; do not duplicate server authorization or state machines in UI code.
- API keys exist only in controlled in-memory input for `/key-usage`. Never write them to URL, storage, cookies, analytics, logs, error breadcrumbs, service-worker caches, or router state.
- Treat aggregate Token counts as decimal strings and use BigInt/arbitrary-precision formatting.
- Implement Loading, Empty, Error, Forbidden, Disabled, and Stale states, not only happy paths.
- Do not add price, payment, balance, purchase, promo, redeem, affiliate, or personal-quota UI.
- Run lint, typecheck, unit/component tests, and build after the M0 scripts exist.
