# Development tools

This area is reserved for contract verification, migration/checksum validation, architecture/forbidden-scope checks, a deterministic mock OpenAI upstream, and local developer utilities.

Tools must not become production runtime dependencies or create alternative OpenAPI, SQL, Redis, or event-contract sources.

When the optional repository-local SDKs are installed under the ignored `.tools/` directory, run commands through `local-dev/run-with-toolchain.sh`. The wrapper scopes .NET, Node, Colima, Lima, Docker and Testcontainers defaults to the invoked command; it does not modify the user's shell profile, Docker credentials or system installation. Explicit caller values such as `DOCKER_CONFIG` and `DOCKER_HOST` always win over repository-local defaults.
