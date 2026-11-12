# Dependency rules

- Domain → nothing.
- Contracts → nothing (it may reference Domain only when intentionally necessary; today it does not).
- Application → Domain, Contracts.
- Configuration → Application, Domain.
- Infrastructure → Application, Domain, Contracts; Configuration only where an implementation needs it (INI documents for workspace and customer files).
- Desktop → Application, Domain, Contracts, Configuration; Infrastructure only from the composition root (`Bootstrap/`, `Composition/`).
- ViewModels never depend on Infrastructure classes, Microsoft.Data.SqlClient, Microsoft.Data.Sqlite, Microsoft.Graph, Microsoft.Identity.Client or IConfiguration; they use Application interfaces and Desktop view services (`IThemeService`, `IDialogService`, `IShellLauncher`).
- Domain never references WPF, configuration, databases, Microsoft authentication/Graph, Serilog or the file system.
- `IServiceProvider` is used only in the composition root and in `PageViewModelFactory` (view resolution).
