# Secure Plus Agent – UI Style Guide

This guide is the reference for every page of the desktop application (WPF, `src/EngineeringWorkspace.Desktop`).
New pages follow it; reviews check against it.

## 1. Capitalisation

| Element | Case | Examples |
|---|---|---|
| Menu sections, groups and items | Title Case | Dashboard, Roadmap, Heat Map, Run Assessment, Baseline Configurations |
| Page and card headings | Title Case | Files and Disk Space, Customer Compliance, Import Preview |
| Buttons, toggles, check boxes | Title Case, verb first | Save Changes, Run Live Assessments Now, Record As Applied, Selected Only |
| Field labels and column headers | Title Case | Customer Name, Next Run, Last Run, Max Concurrent |
| Parenthetical hints after a label | lower case | Secret Lifetime (days), Client Secret (write-only) |
| Help text, captions, status and validation messages | Sentence case, full stop | "Refresh paused while the page is hidden." |
| Fixed helper texts named by the specification | As specified | Max is 20 |

Title Case rules: capitalise every word except *a, an, the, and, or, nor, of, to, in, on, for, with, by, at, from, per,
as, into, vs* when they are neither first nor last. Keep acronyms and product names as written (ID, SQL, CSV, Microsoft
Graph, ConnectWise, Entra). Hyphenated words capitalise each part (Read-Only, Start-Up).

Product vocabulary: **Roadmap** (one word), **Dashboard**, **Specification Comparison** (never "Golden Tenant
Comparison"), **Baseline Configurations**, **Workspace Configuration**, **Database Health**.

## 2. Layout

- A page is a vertical `StackPanel` of cards (`Style="{StaticResource Card}"`). The first card holds the page heading
  (`Text.Heading3`), a one-line explanation (`Text.Secondary` or `Text.Caption`), the filters and the primary actions.
- Primary action first, secondary actions after it with `Button.Secondary`; destructive actions use `Button.Danger` or
  an icon button that opens a confirmation dialog.
- Row actions in grids are icon buttons (`Button.Icon`, Segoe MDL2 glyphs: E70F edit, E8C8 clone, E74D delete) with a
  `ToolTip` and `AutomationProperties.Name`.
- Long grids sit in a `controls:ResizablePanel` so the user can enlarge them.
- KPIs use `controls:KpiPanel` (`KpiTile(Title, Value, Detail)`), charts `controls:BarChart` (`ChartBar`), exports
  `controls:ExportButtons` (.csv, .html, .json, .txt) through the shared `ExportCoordinator` / `IExportService`.
- Empty states are written sentences that say what to do next ("No baselines yet: they are imported with the
  reference data once the database is ready."). A page never shows "Not yet available".

## 3. Shared controls

| Need | Control | Notes |
|---|---|---|
| Free-text search | `controls:SearchBox` | Magnifying glass, placeholder, clear button, 250 ms debounce. Bind `Text` two-way; never a plain `TextBox`. |
| Customer selection on a page | `controls:CustomerSelector` + `CustomerScopedPageViewModel` | Follows the global customer until the user changes it on the page. |
| Multi-value filters | `MultiSelectFilter` | Shows "(All)" when nothing is selected (`emptyMeansAll`). |
| Code (Graph requests, scripts, T-SQL) | `controls:CodeView` | `CodeLanguage` = json, powershell, sql…; offer a Copy button next to it. |
| Confirmation | `IDialogService.Confirm(ConfirmationRequest)` | Title Case title, a sentence, key details and the action verb. `IsDestructive` for deletes. Required before any change outside the application (tenant, server). |

Filters default to "(All)" and the current customer.

- **Header icon pickers** (`Button.HeaderIcon`): icon toggle that opens a popup; Esc closes; the selected value stays visible as text.
- **Task status colours** (Gantt): green = Completed, amber = Staged.

## 4. Colour and theme

Colours come only from theme resources (`DynamicResource`), never hex values in pages:

| Token | Use |
|---|---|
| `AppBrush.Accent` | Brand accent: selection, icons (rocket), wordmark badge, growth bars |
| `AppColor.HeaderBackground` → `AppColor.Secondary` | Header gradient |
| `AppBrush.HeaderForeground` | Text on the header |
| `AppBrush.Success` / `AppBrush.Warning` / `AppBrush.Error` | Status: ≥ 80 % / ≥ 50 % / below; Healthy / Warning / Critical |
| `AppBrush.TextDisabled` | Not evaluated, informational |

Light, dark and system modes are defined in `ApplicationProperties.json` (see
`docs/configuration/application-properties-reference.md`). Charts and status chips use the same tokens so they follow
the theme.

## 5. Branding

The header shows the text wordmark: a **GDS** monogram badge in the accent colour, the application name
(`Branding.ini` `ApplicationName`) and the company name. Logo files are optional: add them under `assets/branding` and
set `LogoLight`, `LogoDark` and `WindowIcon` in `Branding.ini`.

## 6. Accessibility

- Every input, icon button and check box in a grid has `AutomationProperties.Name`.
- Do not convey state by colour alone: status chips carry text (Healthy, Warning, Critical).
- Keyboard: every action is reachable with Tab; default buttons are the primary action of a dialog.

## 7. Data and secrets

- Pages never show, log or export secrets (client secrets, certificates, passwords, connection strings). Secret fields
  are write-only; exports mask or omit them (`ExportService.IsSecretName`).
- Values computed or generated by the application (for example generated remediation content) are labelled as such.

## 8. Terminology

- **Engagement**: one contracted piece of work inside a workspace (SOW, project plan, change requests). Use it everywhere: UI, SOW,
  emails, docs, exports and code, consistent with EaaS v3. Customer documents may say "Engagement (project)" on first use. Do not
  use "Project" for this concept ("Project Plan" is the plan of an engagement). ConnectWise mapping: Engagement ↔ ConnectWise Project.
- **Workspace**: one customer tenant, permanently. **Customer Profile**: who the customer is.
- **Specification** (not "Golden Tenant"); **Baseline Configuration**; **Global Library**; **Reference Catalogue**.
