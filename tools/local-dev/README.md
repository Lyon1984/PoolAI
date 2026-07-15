# Local Compose helpers

1. `prepare-compose.sh` creates idempotent local-only passwords, application
   secrets, a CA, and a `mock-smtp` certificate under ignored
   `.tools/compose/secrets` with restrictive permissions. `.tools`, `compose`,
   and `secrets` are all mode `0700`. Because Compose implements `file:` secret
   sources as bind mounts and cannot remap their uid/gid/mode, only the exact
   files mounted into non-root containers are mode `0644`; the private parent
   path prevents other host users from traversing to them. Unmounted CA
   material remains mode `0600`.
2. It creates `local-compose-ca-bundle.pem` from the host's public root bundle
   plus the local SMTP CA. Api/Worker use that combined bundle through
   `SSL_CERT_FILE`, so trusting the local SMTP certificate does not replace
   public roots. Set `POOLAI_SYSTEM_CA_BUNDLE` only when the host bundle is in a
   nonstandard location.
3. `validate-compose.sh` resolves and checks the topology without starting
   containers.
4. `compose-up.sh` prepares the private source directory, runs static
   validation (including its permission model), requires all three image
   overrides or none, invokes `eng/build/publish-hosts.sh` when local artifacts
   are needed, packages those outputs, starts the topology, and waits for
   dependency health plus one-shot Migrator success.
5. `verify-compose.sh` checks the published loopback mock and Mailpit, then
   drives the mock SNTP endpoint through synchronized, `+6000ms`, `-6000ms`,
   dropped-response, and reset states. It requires Api readiness to move
   `200 → 503 → 503 → 503 → 200` while liveness remains 200 in every failure
   state.

PostgreSQL, Redis, SMTP, and mock SNTP UDP 4123 remain only on the internal
`runtime` network and are not published to the host. Api, mock upstream HTTP,
and the Mailpit UI also join a dedicated bridge whose default host binding is
`127.0.0.1`; only their explicitly declared loopback ports are published. The
second bridge is required because an `internal: true` Docker network has no
connection to host network interfaces.

There is intentionally no teardown or volume-destruction helper. Plain Compose
`down` preserves named volumes and `.tools/compose/secrets`; deleting either is
a separate destructive action that requires an explicit decision.

NTP offset is not controlled by a hidden application environment variable.
Api sends real SNTP packets to `mock-upstream:4123`; the mock's explicit
LocalCompose-only HTTP test control changes its UDP response timestamps or
drops replies. `verify-compose.sh` can reach that control only through the
published loopback mock port. The fixed ±5-second safety boundary remains in
application policy and is never supplied by Compose.
