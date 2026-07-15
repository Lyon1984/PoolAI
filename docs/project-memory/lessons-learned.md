# Lessons learned

## Central package changes require Solution-wide lock refresh

- Evidence: adding a centrally managed direct package with `CentralPackageTransitivePinningEnabled` made otherwise untouched project lock files fail `dotnet restore --locked-mode` with NU1004.
- Durable lesson: after adding or moving a central package version, run a Solution-level `dotnet restore PoolAI.sln --force-evaluate`, then rerun locked restore; refreshing only the directly edited project is insufficient.
- Scope and verification: NuGet/package-lock maintenance; reproduced by the repository quality gate and verified by the subsequent fully passing locked restore.

## Minimal-host tests must inject startup-time settings early

- Evidence: `WebApplicationFactory.ConfigureAppConfiguration` supplied the test PostgreSQL setting too late for top-level `Program.cs` service registration, which reads the connection string immediately after `WebApplication.CreateBuilder`.
- Durable lesson: settings consumed during minimal-host service registration must be injected with `IWebHostBuilder.UseSetting` or an equivalently early provider; later test configuration remains suitable for settings first read during host build or service resolution.
- Scope and verification: Api End-to-End host fixtures; reproduced by seven startup failures and verified by all seven End-to-End tests plus the full quality gate.
