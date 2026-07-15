# Backend-specific instructions

- Read `../AGENTS.md`, `../docs/architecture/design-pattern-baseline.md`, and the Solution DAG in `../docs/开发执行规格-v1.0.md` before changing backend code.
- Keep Api, Worker, and Migrator as non-referencing Hosts with one Composition Root each.
- Implement behavior in its owning Context. Cross-Context calls use declared `*.Abstractions` only.
- Preserve Domain/Application independence from EF Core, Npgsql, Redis, ASP.NET transport, SMTP, and vendor SDKs.
- Do not expose EF entities, `IQueryable`, database transactions, HTTP types, or vendor DTOs across Ports.
- Do not create generic repositories, shared business services, God DbContexts, static service locators, or hidden commits.
- Add architecture tests for every new project reference or loading boundary.
- Never add commercial or personal-quota namespaces, even as placeholders.
