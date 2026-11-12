# Navigation.ini reference

Navigation is configuration-driven: `MainWindow` renders the resolved model; there are no hard-coded buttons.
Resolution: configuration → route catalog (`RouteCatalog`) → feature, workspace, customer and capability policies →
sorted tree → navigation ViewModels → WPF tree.

## Sections

```ini
[Navigation:Sections:<id>]   ; DisplayName, Description, IconKey, SortOrder, IsVisible, IsEnabled, RequiredFeature, ExpandedByDefault, CanCollapse
[Navigation:Groups:<id>]     ; Parent=<section id>, ... ExpandedByDefault, ShowHeader
[Navigation:Items:<id>]      ; Parent=<section or group id>, Route, IsDefault, RequiresWorkspace, RequiresCustomer, RequiredCapability, ...
```

- IDs are stable; display names and order can change freely. Children sort by `SortOrder` (steps of 10), then ID.
- Layers merge by ID (MergeById). A higher layer (environment, workspace, customer, overrides) can override any key
  of an existing ID, add an ID, or remove one with `Removed=true`.
- `IsVisible=false` hides a node. A disabled node (`IsEnabled=false`, its feature off, or its workspace/customer
  requirement not met) is shown greyed with the reason as a tooltip.
- Routes are lowercase and slash-separated, contain no WPF type names and must be registered by the application.
  Unknown, malformed and duplicate routes, duplicate IDs and unknown parents are configuration errors.
- The default route is `[Application] DefaultRoute`; it must be visible, enabled and feature-enabled. At startup the
  shell tries the last route, then the default route, then the item with `IsDefault=true`, then the first enabled item.

## Default tree

| Section (order) | Items (route) |
|---|---|
| Dashboard (100) | Overview `dashboard/overview` (default), Road Map `dashboard/road-map`, Catalog `dashboard/catalog`, Fleet View `dashboard/fleet-view` |
| Assessment (200) | Assessment Scan `assessment/engine`, Live Assessment `assessment/live` |
| Planning (300) | Script Generator `planning/script-generator`, Config File Generator `planning/config-file-generator` |
| Deployment (400) | Change Request `deployment/change-request`, Change Execution `deployment/change-execution`, Tracker `deployment/tracker` |
| Reporting (500) | Control Completion View `reporting/control-completion`, Solution Completion View `reporting/solution-completion`, Specification Comparison (replaces Golden Tenant Comparison) |
| Workspace (600) | Tenant App Registration `workspace/tenant-app-registration`, Customer Profile Import `workspace/customer-profiles/import`, Customer Profile Export `workspace/customer-profiles/export`, Exceptions `workspace/exceptions` |
| Settings (700) | Workspace Configuration Import `settings/configuration-import`, Workspace Configuration Export `settings/configuration-export`, Teams Integration `settings/teams-integration`, ConnectWise Integration `settings/connectwise-integration`, Golden Tenant Configuration `settings/golden-tenant-configuration`, Database Configuration `settings/database-configuration`, Auditing and Logs `settings/auditing-and-logs`, Credential Management `settings/credential-management`, Appearance `settings/appearance` |

Database Configuration, Tenant App Registration, Credential Management, Appearance and Auditing and Logs are real
pages. Every other destination uses the shared placeholder page, which shows its route, section, feature state,
workspace/customer requirement and planned milestone.
