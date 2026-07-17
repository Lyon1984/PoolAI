# Deployment assets

`deploy/compose/compose.yaml` defines the M0 seven-service local topology:
PostgreSQL, Redis, deterministic mock upstream, STARTTLS-only mock SMTP,
one-shot Migrator, Api, and Worker. Api/Worker wait for the Migrator to complete
successfully; no executable Host references or embeds another Host.

The existing mock-upstream service also provides an internal SNTP v4 UDP
endpoint on port 4123. It is not an eighth service and is not host-published;
its LocalCompose-only failure control remains on the loopback-published mock
HTTP port so readiness, liveness isolation, and recovery can be verified.

The Host Dockerfiles consume pre-published artifacts and never restore or build
application source. Business migrations remain single-sourced in
`docs/database/` and are executed only by `PoolAI.Migrator`. The PostgreSQL
bootstrap here creates cluster roles only.

Tracked files contain no credentials or private keys. Local values are generated
under ignored `.tools/compose/secrets`, and Compose refuses interpolation unless
`POOLAI_SECRET_DIR` is explicitly set. Normal `down`/`up` preserves all named
volumes; this repository intentionally contains no volume/database destruction
helper.

The Api receives dedicated, independently generated secret files for JWT signing,
refresh-token hashing, TOTP recovery-code hashing, login-IP rate-limit scoping,
password-reset scoping, one-time-token hashing, API-key hashing, idempotency, and
envelope encryption. The Worker does not mount or require the Identity Api
secrets.

Use `tools/local-dev/prepare-compose.sh` followed by
`tools/local-dev/compose-up.sh`. Static topology validation is available through
`tools/local-dev/validate-compose.sh` and does not start containers.
