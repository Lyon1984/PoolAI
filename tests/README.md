# Test suites

The frozen test topology contains exactly these .NET projects:

- `PoolAI.UnitTests`
- `PoolAI.ArchitectureTests`
- `PoolAI.ContractTests`
- `PoolAI.IntegrationTests`
- `PoolAI.EndToEndTests`
- `PoolAI.LoadTests`

Concurrency, migration, adapter, fault, and security scenarios live as folders/traits within the appropriate suite. Contract tests read the authoritative OpenAPI, error catalog, fixtures, SQL, and Redis contracts from `docs/`; they do not maintain copied test-data sources.
