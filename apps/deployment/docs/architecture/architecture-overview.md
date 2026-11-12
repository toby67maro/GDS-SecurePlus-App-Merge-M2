# Architecture overview

Secure Plus - Modern Work (solution `EngineeringWorkspace`) is a .NET 10 WPF desktop application built on the Generic
Host (`Host.CreateApplicationBuilder`), Microsoft.Extensions DI/Configuration/Logging/Options, CommunityToolkit.Mvvm
and Serilog (as an `ILogger` provider only).

| Project | Responsibility |
|---|---|
| `EngineeringWorkspace.Domain` | Entities, value objects (WorkspaceId, CustomerId, SecretReference), enums, invariants. No framework dependencies. |
| `EngineeringWorkspace.Contracts` | Versioned import/export contracts (`CustomerProfileExportV1`, `WorkspaceConfigurationExportV1`). |
| `EngineeringWorkspace.Application` | Use cases, orchestration and ports: navigation, features, workspace and customer context, credentials, database status, tenant connection, permission validation, app-registration assistant, audit, redaction; interface-only ports for assessments, scripts, reporting, Golden Tenant comparison, integrations, serialization. |
| `EngineeringWorkspace.Configuration` | Layer discovery and precedence, INI diagnostics, merging, provenance, binding, four-stage validation, immutable snapshot, INI writer, ApplicationProperties.json schema and loader. |
| `EngineeringWorkspace.Infrastructure` | File stores, DPAPI secret store, credential metadata, JSON-lines audit, SQL Server/Azure SQL/SQLite provisioning and migrations, MSAL and Microsoft Graph, policy repository scanner. |
| `EngineeringWorkspace.Desktop` | Composition root and startup/shutdown, shell, navigation rendering and view resolution, pages and ViewModels, theme service, dialogs. |

Startup order: bootstrap arguments → directories and environment → configuration (layer, bind, validate) → logging →
service registration → host start → appearance and theme → navigation → workspace/customer restore → main window and
navigation. Nothing at startup creates or alters a database; the database status check is asynchronous and read-only.
Shutdown: save shell and workspace state → `ApplicationStopped` audit → `StopAsync` → dispose.

See `dependency-rules.md`, `theme-system.md`, the EaaS v3 documents and the ADRs in `architecture-decisions/`.
