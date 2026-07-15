# Operations

`ops/` contains post-deployment runbooks, reviewed operational scripts, and local environment metadata. It is separate from `deploy/`: deployment creates/runs a release; operations diagnose, migrate, restore, rotate, and respond after deployment.

Do not store credentials, dumps, private keys, or secret values here. Environment-specific connection metadata must remain ignored and least-readable. Destructive operations require explicit authorization, a preflight check, and post-operation verification.
