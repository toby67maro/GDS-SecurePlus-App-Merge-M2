# Configuration (INI) reference

## Flow and precedence

INI files → providers (line-level diagnostics; `Microsoft.Extensions.Configuration.Ini` parses values) → layers →
merge with provenance → typed settings records → binding validation → options validation → cross-section validation →
immutable snapshot registered in DI. Consumers receive typed settings (`ApplicationSettings`, `DatabaseSettings`, ...);
no ViewModel or ordinary service reads `IConfiguration`.

Precedence is defined once in `src/EngineeringWorkspace.Configuration/Layering/ConfigurationPrecedence.cs`
(lowest to highest):

| Order | Layer | Location |
|---|---|---|
| 10 | Compiled defaults | `Loading/CompiledDefaults.cs` (the only place defaults are defined) |
| 20 | Application.ini (required) | configuration directory |
| 30 | Branding.ini | configuration directory |
| 40 | Features.ini | configuration directory |
| 50 | Navigation.ini | configuration directory |
| 55 | Repositories.ini | configuration directory (kept from the foundation milestone) |
| 60 | Integrations.ini | configuration directory |
| 70 | Database.ini | configuration directory |
| 80 | Logging.ini | configuration directory |
| 90 | GoldenTenant.ini | configuration directory |
| 100 | Environments/{Environment}.ini | configuration directory |
| 110 | Workspace.ini | `data/Workspaces/{Workspace}/Workspace.ini` |
| 120 | Customer.ini | `data/Customers/{Customer}/Customer.ini` |
| 130 | Overrides.ini | `data/Customers/{Customer}/Overrides.ini` |
| 140 | Environment variables | `ENGINEERINGWORKSPACE_<SECTION>__<KEY>` (double underscore = `:`), e.g. `ENGINEERINGWORKSPACE_DATABASE__PROVIDER=SQLite` |
| 150 | Command line | `--Section:Key=value`; switches `--environment`, `--workspace`, `--customer`, `--config-dir`, `--data-dir` |

The **configuration directory** is `%LOCALAPPDATA%\Global Data Systems\Secure Plus - Modern Work\config` unless
`--config-dir` is given; package templates are copied there on first run and never overwritten (the install folder
is read-only). The environment comes from the command line, then environment variables, then `Application.ini`.
The workspace and customer layers apply to the workspace and customer restored at startup (or named with
`--workspace`/`--customer`); changing them at runtime takes effect at the next start (configuration reload: M2).
Environment files never hold secrets.

## Merging

Scalars replace. Collections follow `CollectionMergePolicy`: navigation sections, groups and items merge by stable ID
(**MergeById**; `Removed=true` removes an ID — **RemoveById**). Indexed collections (`Section:List:0`, `:1`, ...)
are **Replace**d as a whole by a higher layer unless declared **Append**. Display names never identify elements.

## Validation

All four stages run and every error is collected in one pass. Each error has FilePath, SourceLayer, Section, Key,
RawValue (redacted when displayed), ErrorCode, Message, ExpectedFormat, AllowedValues and Severity. Any error stops
startup with a safe error window and a bootstrap diagnostic file; warnings are logged and listed on
**Workspace > Settings > Auditing and Logs**.

1. **Files/providers** – missing `Application.ini`, unreadable file, non-UTF-8 encoding, malformed section, line
   without `=`, key outside a section, duplicate key, invalid command line.
2. **Binding** – booleans, integers and ranges, enums, GUIDs, URIs, paths, secret references.
3. **Options** – application ID/name, environment (`Development`, `Test`, `Staging`, `Production`), log level, export
   format, secret-store provider, database provider-specific fields and SQL identifiers, timeouts, navigation parents,
   routes (format, registered, duplicates), duplicate IDs, unknown features, logo/icon file types, tenant/client/
   service principal ID formats in customer layers, removed `IncludeSensitiveValues`.
4. **Cross-section** – default route registered, visible, enabled and feature-enabled; unknown feature keys; Teams or
   ConnectWise feature on while the integration is disabled; Golden Tenant enabled without a baseline; plaintext in
   secret-bearing keys (`*Password`, `*Secret`, `*Token`, `*ConnectionString`, ... and `Password=` inside values);
   startup workspace missing with `CreateDirectoryIfMissing=false`; credential store inside a Git working tree;
   automatic database options in Production; theme keys left in `Branding.ini`; missing branding images.

Missing optional settings use the compiled defaults; a malformed explicit value is an error.

## Files and keys

### Application.ini

`[Application]` Id, Name, Title, CompanyName, Environment, DefaultRoute, RestoreLastWorkspace, AllowMultipleInstances.
`[Paths]` ConfigurationDirectory, DataDirectory, WorkspaceDirectory, CustomerDirectory, GoldenTenantDirectory,
ImportDirectory, ExportDirectory, LogDirectory, AuditDirectory, AssetDirectory, CredentialDirectory, StateDirectory
(relative paths resolve against the per-user data root; AssetDirectory against the application folder).
`[Workspace]` RequireWorkspace, CreateDirectoryIfMissing, RestoreLastWorkspace, AllowCustomerlessWorkspace, DefaultWorkspace.
`[ImportExport]` DefaultExportFormat (`Json`), OverwriteExistingFiles, IncludeMetadata, TimestampFileNames.
`IncludeSensitiveValues` was removed: secrets are never exportable.
`[Credentials]` ExpirationWarningDays (1-365, default 30), SecretStoreProvider (`Dpapi`).

### Branding.ini

`[Branding]` ApplicationName, ApplicationTitle, CompanyName, LogoLight, LogoDark, WindowIcon. Appearance lives in
`ApplicationProperties.json` (see `application-properties-reference.md`).

### Features.ini

`[Features]` one boolean per stable feature ID: Dashboard, Assessment, LiveAssessment, Planning, ScriptGenerator,
ConfigFileGenerator, Deployment, Reporting, GoldenTenant, CustomerProfiles, WorkspaceConfiguration, TeamsIntegration,
ConnectWiseIntegration, DatabaseConfiguration, CredentialManagement, Auditing, RepositoryCatalog, JsonAutomation, Appearance.

### Integrations.ini

`[Integrations:Teams]` Enabled. `[Integrations:ConnectWise]` Enabled, Site, CompanyId.
`[Integrations:Microsoft]` AdministratorClientId (public-client app used by the app-registration assistant; empty
disables it), AdministratorRedirectUri (default `http://localhost`).

### Database.ini

Written by **Database Configuration** to the per-user copy of `Database.ini`. Existing key names are unchanged.

| Key | Values |
|---|---|
| Provider | `None`, `SQLite`, `SqlServer`, `AzureSql` |
| ServerName, SqlInstance, Port | server host, optional named instance, optional port |
| DatabaseName | database name (`^[A-Za-z_][A-Za-z0-9_]{0,127}$`), or SQLite file path |
| Authentication | `WindowsIntegrated`, `SqlLogin`, `EntraInteractive`, `EntraServicePrincipal`, `EntraManagedIdentity`, `None` |
| UserName, PasswordSecretReference | SQL login; the password is stored with Credential Management, only its reference here |
| EntraClientId, EntraTenantId, EntraClientSecretReference, ManagedIdentityClientId | Microsoft Entra options |
| EncryptConnection, TrustServerCertificate | TLS (Encrypt=Mandatory when true) |
| ConnectionTimeoutSeconds (1-300), CommandTimeoutSeconds (1-3600), RetryCount (0-255), RetryDelaySeconds (1-60) | timeouts and connect retry |
| ConnectionStringSecretReference | optional full connection string held in the secret store |
| AutomaticDatabaseInitialization, AutomaticSchemaUpgrade | off by default; never honoured in Production |

### Logging.ini

`[Logging]` MinimumLevel (`Verbose` ... `Fatal`), RetainedFileCountLimit, FileSizeLimitMegabytes.

### GoldenTenant.ini

`[GoldenTenant]` Enabled, BaselineId, ConditionalAccessBaselineRepository, PolicySetsDirectory.

### Workspace.ini and Customer.ini

Written by the application. `Workspace.ini`: `[Workspace]` Id, Name, CurrentCustomerId, TenantId, Environment,
DatabaseReference, GoldenTenantReference, CreatedAtUtc, ModifiedAtUtc; `[Integrations]`. `Customer.ini`:
`[Customer]` Id, Name, Environment, AdditionalDomains, Tags, Notes; one `[Tenant:<key>]` per tenant with DisplayName,
TenantName, TenantId, PrimaryDomain, CloudEnvironment, ClientId, ServicePrincipalId, AuthenticationType,
ClientSecretReference, CertificateReference, CredentialExpiration, RequiredCapabilities; optional `[Contact:<n>]`.
Other sections in these files override application settings for that workspace/customer.
