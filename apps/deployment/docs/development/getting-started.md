# Getting started

1. Install the .NET 10 SDK (any 10.0 feature band; see `global.json`) on Windows.
2. `dotnet build` from the repository root, then `dotnet run --project src/EngineeringWorkspace.Desktop -- --config-dir .\config`
   to use the repository configuration directly.
3. Optional local database: run `build/scripts/Install-LocalSqlServer.ps1` elevated, or choose SQLite on
   **Workspace > Settings > Database Configuration**.
4. Regenerate the EaaS v3 schema only when the v3 data dictionary changes:
   `python build/scripts/eaas-schema/generate_eaas_v3_schema.py` (see `schema/eaas-v3/SOURCE.md`).

Coding rules: `coding-standards.md`. Architecture: `docs/architecture/architecture-overview.md`.
