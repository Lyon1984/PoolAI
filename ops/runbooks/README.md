# Runbooks

This directory contains reviewed, reusable post-deployment procedures. A runbook is an execution control, not authorization to mutate an environment: the operator must still have an approved change, an explicit target, a recovery boundary, and the least-privileged identity required by the procedure.

Never paste credentials, key material, decrypted payloads, database dumps, private host details, or raw production envelopes into a runbook or its evidence.

- [`secret-envelope-key-rotation-and-restore.md`](secret-envelope-key-rotation-and-restore.md) — Envelope v1 KEK rotation, authenticated CAS rewrap, isolated restore proof, and old-key retirement.
- [`quota-reconciliation.md`](quota-reconciliation.md) — preserve evidence, classify authoritative/projection/delivery divergence, explicitly isolate a Group, perform bounded recovery, and verify zero divergence before re-enabling.
