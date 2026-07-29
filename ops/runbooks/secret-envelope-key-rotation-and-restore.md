# Envelope v1 key rotation and restore

## Status and scope

This runbook governs reversible PoolAI Envelope v1 secrets, including Supply-owned Account credentials. It covers KEK introduction, current-key switching, authenticated DEK rewrap, retained-backup validation, rollback, and historical-key retirement.

It does not authorize a production change. Every execution needs an approved change window, an explicitly named environment, separate key-management authorization, a recent recoverable backup, and a recorded rollback owner.

The current M2-E1 review candidate provides strict Envelope v1 cryptographic
transformation, Supply-owned Account create/replace persistence, a primary-key
selector, an internal credential-revision CAS, and a default-disabled Worker
command. ADR 0009 and the forward database migration are still unsigned, and
the candidate has not been authorized against a remote environment. Until both
approvals and their exact evidence are recorded, stop after local isolated
validation: do not enable the Worker, run the migration remotely, execute a
manual SQL rewrite/decrypt-and-re-encrypt loop, or use an ad hoc production
script.

## Invariants

- Envelope fields remain exactly `v/alg/kid/wrapped_dek/wrap_nonce/wrap_tag/ciphertext/nonce/tag`; `v=1` and `alg=A256GCM+A256GCM-v1`.
- Account credential AAD is rebuilt from trusted record identity as `poolai|v1|account-credential|account|<account UUID>|credential_envelope`. Stored data never supplies the authoritative purpose, entity, or field.
- A new write uses a fresh 256-bit DEK plus independent content and wrapping nonces.
- Rewrap authenticates both layers, preserves `ciphertext/nonce/tag`, and replaces only `kid/wrapped_dek/wrap_nonce/wrap_tag`.
- Key selection is exact by `kid`; unknown key, version, algorithm, shape, AAD, or tag fails closed and raises the redacted security event.
- The current key and every historical key referenced by live rows or any retained backup stay in every required reader keyring.
- Api and Worker must receive the same validated keyring generation before `current` changes. Migrator never loads keys and never decrypts or rewraps application data.
- No transaction spans secret-provider access, process restart, backoff, alert delivery, or backup/restore work.
- `accounts.credential_revision` is the maintenance CAS token. Human
  replacement advances both it and the public Account version; maintenance
  rewrap advances only credential revision and leaves public version,
  `updated_at`, prefix/hint, health, cooldown, and lifecycle unchanged.
- The maintenance database guard requires content `ciphertext`, `nonce`, and
  `tag` to remain unchanged. The Worker role cannot directly update the
  envelope or credential revision, and the NOLOGIN function owner cannot read
  the stored envelope.
- Logs, metrics, traces, audit payloads, tickets, and evidence never include key material, plaintext, a full envelope, authentication data, or private configuration.

## Required roles and evidence

Record these non-secret facts in the approved change:

1. change owner, security reviewer, database operator, restore operator, and rollback owner;
2. release commit and signed ADR/runbook revision;
3. target environment and affected Envelope purposes;
4. opaque new/old key identifiers, without key material;
5. live-row inventory count by `kid`;
6. retained backup set and retention horizon;
7. last successful isolated restore evidence;
8. alert channel health and the baseline failure-event count;
9. bounded batch size, pause threshold, retry limit, and rollback decision deadline.

The security reviewer and database operator must be different approval steps even if one person holds both duties.

## Phase 0 — preflight

1. Verify the exact release containing the approved Envelope implementation,
   separately approved forward migration, internal credential revision, exact
   function ACLs, and CAS workflow.
2. Verify all required readers reject missing, mismatched, duplicate, or ambiguous current/history key configuration at startup.
3. Prove the new key exists in the approved secret provider and is exactly 256 bits without printing, exporting, hashing into logs, or otherwise exposing it.
4. Inventory live Envelope rows by parsed, bounded `kid` only. Treat malformed documents as security failures; do not repair them during rotation.
5. Identify every retained database backup, replica/DR copy, delayed restore point, and export that may contain an old-key envelope.
6. Produce a new recoverable backup and verify its catalog/checksum metadata through the existing backup system.
7. Confirm no schema migration, deployment, incident response, or second key rotation overlaps the window.
8. Confirm the production rewrap command uses a Supply-owned record reader and CAS writer. If no reviewed command and execution identity exist, stop.

## Phase 1 — add the new historical-readable key

1. Add the new key to `Secrets:Envelope:DecryptKeyRing` for every Api and Worker instance while leaving `Secrets:Envelope:CurrentKeyId` and `Secrets:Envelope:CurrentKey` unchanged.
2. Roll instances through the normal deployment path; never edit one process in place.
3. Require every instance to pass configuration validation and readiness.
4. Run only non-secret probes: current-key identifier consistency, expected ring membership, successful reads of approved synthetic fixtures, and zero new validation failures.
5. Roll back the deployment if any reader lacks the old or new key. No data has changed in this phase.

## Phase 2 — switch the current key

1. Set `Secrets:Envelope:CurrentKeyId` and `Secrets:Envelope:CurrentKey` to the new ring entry in one reviewed configuration revision.
2. Roll all required writers/readers and require readiness before proceeding.
3. Create and replace approved synthetic credentials. Verify their envelopes name the new key and that responses, logs, traces, and audit payloads contain no credential or envelope.
4. Verify existing old-key records still decrypt through the retained historical entry.
5. Pause on any `supply.account_credential_envelope_validation_failed` event or readiness failure. Do not begin rewrap until the cause is resolved.

Rollback before rewrap: restore the old current-key selection while retaining both keys in the ring. New-key records remain readable because the new key is still historical-readable.

## Phase 3 — authenticated CAS rewrap

This phase may run only through the reviewed Supply-owned workflow. It must:

1. acquire the dedicated PostgreSQL session advisory lock and select a bounded
   primary-key keyset batch without holding a transaction during cryptography
   or backoff;
2. read `account_id`, the complete stored envelope, and
   `credential_revision`; scan every retained Account rather than filtering
   rows by database-parsed `kid`;
3. rebuild Account AAD from the trusted `account_id`;
4. authenticate and rewrap with the current key;
5. verify that `ciphertext`, `nonce`, and `tag` are byte-for-byte unchanged;
6. verify session-lock ownership, then open one short PostgreSQL Unit of Work
   and update only `credential_envelope` plus
   `credential_revision = credential_revision + 1`, comparing the exact
   expected credential revision;
7. commit once; a zero-row update is a concurrency miss and must be reread, not overwritten;
8. clear temporary buffers, emit only aggregate counts, and use bounded retry/backoff outside the transaction.

Stop the batch on any authentication failure, unknown key/version/algorithm, malformed document, unexpected plaintext decode, CAS error-rate threshold, alert-delivery failure, or database dependency failure. Preserve the row unchanged for investigation.

Crash/retry must be safe: an already-current envelope is authenticated and then
is a no-op; a crash after commit is rediscovered as that no-op; a concurrently
replaced credential advances credential revision and wins over stale rewrap
output. A CAS miss ends the short transaction, rereads the new snapshot, and
permits at most the reviewed fixed number of recomputations.

Rollback after rewrap: keep both keys available and switch the old key back to current if new writes must stop. Do not attempt to reverse already rewrapped rows merely to make their `kid` uniform.

## Phase 4 — inventory and isolated restore

1. Re-inventory live rows. The old-key count must reach zero without malformed or unknown-key rows.
2. Keep the old key while any retained backup can contain it.
3. Restore each release-required backup class into an isolated, access-controlled environment with outbound network access disabled.
4. Supply the minimum approved restore keyring through the restore environment's secret provider; never copy keys into the backup, command line, evidence bundle, or repository.
5. Validate representative and boundary records by rebuilding exact AAD from restored trusted identifiers. Record counts and pass/fail classifications only.
6. Rewrap in the isolated environment only if the restore exercise explicitly tests that path. Never treat an in-memory fixture or live-row count as physical backup evidence.
7. Destroy the isolated restore environment and secret grants under the separate restore procedure, retaining only non-secret evidence.

M2-E1 local JSON round-trip tests prove the cryptographic restore behavior. Physical PostgreSQL backup/PITR, RPO/RTO, and operator-only execution evidence remain M6-E4 release work.

## Phase 5 — historical-key retirement

Retirement requires a separate go/no-go approval after all of these are true:

- live rows reference no old key;
- all retained backups that can reference it have expired, been rewrapped through an approved process, or passed isolated restore with the planned retirement state;
- DR/replica inventories agree;
- the observation window contains no unknown-key or authentication failures;
- rollback no longer depends on the old key;
- security, database, restore, and service owners have signed the non-secret evidence.

Remove the old key from the decrypt ring only in a new configuration revision. Roll readers, verify readiness and approved synthetic/live probes, then revoke or schedule destruction in the external key system under its own authorization. Never delete key material first.

## Failure response

For any validation event:

1. stop rewrap and current-key changes;
2. preserve the affected row and concurrency metadata without copying the full envelope into tickets or logs;
3. classify only the stable failure code and affected record identifier;
4. verify alert delivery; alert failure is itself fail-closed;
5. determine whether the cause is configuration drift, stale CAS, malformed storage, unauthorized copying, or key loss;
6. invoke the security-incident runbook for suspected tampering or disclosure;
7. resume only from a newly approved checkpoint.

## Completion record

The evidence bundle contains only:

- approved change and reviewers;
- release commit and runbook revision;
- start/end times and target environment label;
- per-phase readiness result;
- aggregate scanned/changed/no-op/CAS-miss/failure counts;
- live and backup inventory by opaque `kid`;
- isolated restore identifier and pass/fail summary;
- alert baseline/final counts;
- rollback or retirement decision.

It must not contain secret-provider values, database credentials, raw dumps, plaintext, full AAD values, serialized envelopes, or private host material.
