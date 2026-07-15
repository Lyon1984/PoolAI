# PostgreSQL cluster bootstrap

The files in this directory provision only the cluster roles required before
`PoolAI.Migrator` runs:

- `poolai_runtime_owner NOLOGIN`
- `poolai_api LOGIN`
- `poolai_worker LOGIN`
- the local deployment identity `poolai_migrator LOGIN`

The database is transferred to `poolai_migrator`, which may explicitly
`SET ROLE poolai_runtime_owner`; Api and Worker are forcibly excluded from that
membership. Passwords are read from Docker secret files into the `psql` process
environment and are never embedded in tracked SQL or command arguments.

No file here copies or executes a business migration. The immutable order,
checksum history, advisory lock, and application schema remain owned by
`PoolAI.Migrator` and the SQL sources under `docs/database/`.
