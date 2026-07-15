# Backend source

This area contains the .NET 10 modular-monolith Solution projects frozen in
`docs/开发执行规格-v1.0.md` and listed in
`docs/architecture/repository-structure.md`.

The three executable Hosts remain separate. Business modules expose narrow
`*.Abstractions` assemblies and hide Domain/Application/Infrastructure/Endpoints
implementation details. Project references are enforced by the M0 architecture
tests; a project or directory alone is not evidence that its production behavior
has been implemented.
