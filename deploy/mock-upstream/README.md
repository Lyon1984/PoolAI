# Deterministic OpenAI-compatible upstream

`server.mjs` is a dependency-free Node test double used only by local Compose.
It exposes `/healthz`, `/v1/models`, `/v1/responses`, and
`/v1/chat/completions` with fixed JSON/SSE output. The same process also serves
SNTP v4 on the internal high UDP port 4123. It returns the client's transmit
timestamp as the originate timestamp and uses the standard receive/transmit
fields, so the application exercises a real UDP probe rather than an injected
offset value. It never logs request bodies or authorization headers.

Tests select a deterministic failure through `X-PoolAI-Mock-Scenario`:

- `success` (default)
- `http-401`, `http-403`, `http-429`, or `http-500`
- `stream-error`
- `disconnect-after-event`
- `usage-out-of-range`

This is a transport double, not an alternate source for the public PoolAI
contract. Golden PoolAI fixtures remain under `docs/contracts/fixtures/`.

In `LocalCompose` only, `POST /test-control/ntp` accepts these deterministic
bodies through the HTTP port published on host loopback:

- `{"mode":"offset","offsetMilliseconds":6000}` (or `-6000`)
- `{"mode":"drop"}`
- `{"mode":"reset"}`

The control endpoint is unavailable outside `LocalCompose` and rejects a
non-loopback Host header. UDP 4123 is exposed only on the internal Compose
network and is never published to the host. Run
`node deploy/mock-upstream/server.mjs --self-test` to verify the SNTP packet
mode/version, originate echo, and four-timestamp offset calculation without
starting a listener.
