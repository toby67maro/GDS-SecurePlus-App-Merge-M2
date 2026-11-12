# Theme system

## Ownership

| Concern | Owner |
|---|---|
| Theme mode, user selection, colors, typography, layout | `config/ApplicationProperties.json` (JSON) |
| Product identity: ApplicationName, ApplicationTitle, CompanyName, logo and window-icon paths | `config/Branding.ini` |
| Reading (System.Text.Json), binding, validation, fallback, diagnostics | `EngineeringWorkspace.Configuration.Appearance` (`AppearanceSchema`, `ApplicationProperties`, `ApplicationPropertiesLoader`) |
| Converting effective properties to WPF resources, applying and switching themes | `EngineeringWorkspace.Desktop.Theming` (`IThemeService`, `ThemeManager`, `ThemeResourceBuilder`) |

`ThemeManager` is the single theme service (the foundation's `ThemeManager` extended, not replaced). Its WPF-free
contract `IThemeService` exposes `CurrentMode`, `EffectiveMode`, `AvailableModes`, `AllowUserSelection`, `Properties`,
`ApplyTheme()`, `SetThemeModeAsync()`, `ReloadPropertiesAsync()`, `UseBuiltInDefaultsAsync()`, `GetEffectiveTheme()`
and `ThemeChanged`. Views and ViewModels never parse JSON.

`DefaultTheme`, `AllowThemeSelection` and `[Branding.Colors]` were moved out of `Branding.ini`; if they remain there,
configuration validation reports a `MovedToApplicationProperties` warning and ignores them. Missing logo or icon files
produce a `BrandingAssetMissing` warning and never stop the application.

## Built-in GDS defaults and their sources

`https://www.getgds.com` could not be reached from the build environment (DNS/network policy) and most GDS Office
documents in the Documents repository are protected with Azure Information Protection, so no colors were taken from
the website and none were guessed. The palette is derived from colors used in GDS-authored files in
`toby67maro/Documents` (commit `f10e83b`):

| Color | Source | Used as |
|---|---|---|
| `#002060` (GDS navy) | `EaaS/GDS Structure.vsdx` (dominant fill, 20 shapes); `M365_Services_Solutions_WIP_TB.xlsx` styles | Light primary, header and navigation; dark header |
| `#156082` (teal) | `EaaS/Secure Plus Application Model.vsdm` | Secondary, buttons, selected navigation |
| `#115472` (deep teal) | `EaaS/Secure Plus Application Model.vsdm` | Light status bar, button hover; dark pressed |
| `#72E1F5`, `#AAEDF9`, `#D3F1FC` (cyan tints) | `EaaS/Secure Plus Application Model.vsdm` | Accent; dark primary, accent and navigation text |
| `#E4F2F5` | `EaaS/GDS Structure.vsdx` | Light secondary surface |
| `#4D4D4D`, `#7F7F7F`, `#BFBFBF` | `Secure Plus Application Model.vsdm`, `GDS Structure.vsdx`, `Customer_Deployment_Checklist_v2.0_Marquie.xlsx` | Secondary text, disabled text, borders |

Other values are tints/shades derived from those colors for a dense, readable engineering UI (backgrounds, hover,
dark surfaces). Status colors (success, warning, error) are not brand colors; they follow Microsoft Fluent semantic
values chosen for at least 4.5:1 contrast. Every default foreground/background pair checked by the loader meets
4.5:1 in both variants. Replace the defaults in `AppearanceSchema.cs` if an approved GDS brand guide becomes
available; that file is the only place they are defined.

## Tokens and WPF resources

`ThemeResourceBuilder` builds one resource dictionary that `ThemeManager` inserts (or replaces) at index 0 of
`Application.Resources.MergedDictionaries`. All views use `DynamicResource`, so a change applies live to the shell,
navigation, header, status bar, forms, buttons, inputs, tables, dialogs and pages without per-ViewModel code.

| Resource key | Type | From |
|---|---|---|
| `AppColor.<Token>`, `AppBrush.<Token>` (PascalCase, e.g. `AppBrush.NavigationSelectedBackground`) | `Color`, frozen `SolidColorBrush` | `appearance.<variant>.colors.<token>` (34 tokens) |
| `AppFont.Family` | `FontFamily` (with Segoe UI fallback) | `typography.fontFamily` |
| `AppFont.Size.{Caption, Small, Base, Navigation, NavigationGroup, Heading1, Heading2, Heading3, StatusBar, Button}` | `double` | `typography.*FontSize` |
| `AppFont.Weight.{Normal, Medium, Heading}` | `FontWeight` | `typography.*FontWeight` |
| `AppLayout.{NavigationWidth, NavigationCollapsedWidth, HeaderHeight, StatusBarHeight, ControlHeight, ButtonMinHeight, InputMinHeight}` | `double` | `layout.*` |
| `AppSpacing.{XS, S, M, L, XL}` | `Thickness` | `layout.spacing*` |
| `AppCorner.{Default, Large}` | `CornerRadius` | `layout.cornerRadius`, `layout.largeCornerRadius` |

Control styles in `src/EngineeringWorkspace.Desktop/Themes/Controls.xaml` reference only these keys. They keep focus
indication (accent focus border/visual), disabled states, the active navigation marker and status colors distinct.
State colors use style triggers (`Text.State`) rather than converters so they also update live.

## Light and dark themes, System mode and switching

Precedence: built-in GDS defaults < `ApplicationProperties.json` < runtime mode selection.

- **Light / Dark** apply `appearance.light` or `appearance.dark` colors; typography and layout are shared.
- **System** reads the Windows app theme (`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`)
  and re-applies automatically when Windows raises a general user-preference change.
- The header selector and the Appearance page change the mode for the session only, and only when
  `allowUserSelection` is true. The JSON file is never rewritten by a mode change.
- Each change is logged and recorded as a `ThemeChanged` audit event.

## Validation and fallback

See `docs/configuration/application-properties-reference.md` for every property, its range and the fallback policy.
Malformed JSON or an unsupported `schemaVersion` ignores the file; an invalid value falls back individually; unknown
properties are ignored; low contrast warns; unreadable contrast (< 1.5:1) resets that pair. Nothing in appearance can
stop the application from starting.

## Appearance page

**Workspace > Settings > Appearance** shows the selected and effective mode, the properties file path and status
(*Built-In GDS Default* or *Custom ApplicationProperties.json*), validation warnings and the effective colors, and offers:

- **Reload Appearance** — re-read, validate, resolve, apply and report (no file watcher).
- **Open Properties Location** — opens the configuration folder.
- **Reset to Built-In GDS Defaults** — applies the defaults for the session after confirmation; the file is not deleted or changed.
- **Create Properties File From Current Defaults** — writes the complete defaults as standard JSON; asks before overwriting an existing file.
