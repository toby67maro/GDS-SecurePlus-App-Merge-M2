# ApplicationProperties.json reference

`ApplicationProperties.json` controls appearance only: theme mode, user theme selection, colors, typography and layout.
Product identity (application and company name, logo, window icon) stays in `Branding.ini`; everything else stays in the
INI files.

- **Location:** `%LOCALAPPDATA%\Global Data Systems\Secure Plus - Modern Work\config\ApplicationProperties.json` (the
  configuration directory; `--config-dir` changes it). The package copy under the install folder is a template copied
  there on first run and never overwritten.
- **Format:** standard JSON (no comments, no trailing commas), camelCase names. Every property except `schemaVersion` is
  optional, so a file can override a single value.
- **Single source of defaults:** `src/EngineeringWorkspace.Configuration/Appearance/AppearanceSchema.cs` (this table is
  generated from it). Color sources: `docs/architecture/theme-system.md`.

## Fallback policy

Appearance problems never block startup.

| Problem | Behaviour |
|---|---|
| File missing | Supported: built-in GDS defaults; information diagnostic. |
| Malformed JSON, root not an object, missing/unsupported `schemaVersion` | File ignored; built-in defaults; warning diagnostic. |
| Invalid value (wrong type, out of range, NaN/Infinity, bad color, unknown font weight) | That value falls back to its built-in default; warning with path, value and expected format, for example `appearance.dark.colors.primary: 'banana' Invalid color; the built-in default #72E1F5 is used. Expected #RRGGBB or #AARRGGBB.` |
| Unknown property | Ignored; information diagnostic. |
| Contrast below 4.5:1 on a key foreground/background pair | Warning; colors kept as configured. |
| Contrast below 1.5:1 (text effectively invisible) | Both colors of that pair fall back to the built-in defaults; warning. |

Diagnostics are written to the application log and listed on **Workspace > Settings > Appearance**.

## Properties

| Path | Type | Default | Allowed values | Min / Max | Description | Example |
|---|---|---|---|---|---|---|
| `schemaVersion` | integer | — (required) | `1` | — | Schema version. Any other value ignores the file. | `"schemaVersion": 1` |
| `appearance.theme.mode` | string | `System` | `Light`, `Dark`, `System` | — | Theme mode. System follows the Windows app theme. | `"mode": "Dark"` |
| `appearance.theme.allowUserSelection` | boolean | `true` | `true`, `false` | — | Show the theme selector in the header and on the Appearance page. | `"allowUserSelection": false` |
| `appearance.typography.fontFamily` | string | `Segoe UI` | 1-200 characters | — | Font family (Segoe UI is the fallback). | `"fontFamily": "Segoe UI Variable"` |
| `appearance.typography.baseFontSize` | number | `14` | 9-32 | 9 / 32 | Body text. | `"baseFontSize": 14` |
| `appearance.typography.smallFontSize` | number | `12` | 9-32 | 9 / 32 | Small text. | `"smallFontSize": 12` |
| `appearance.typography.captionFontSize` | number | `11` | 9-32 | 9 / 32 | Captions. | `"captionFontSize": 11` |
| `appearance.typography.navigationFontSize` | number | `14` | 9-32 | 9 / 32 | Navigation items. | `"navigationFontSize": 14` |
| `appearance.typography.navigationGroupFontSize` | number | `13` | 9-32 | 9 / 32 | Navigation section and group headers. | `"navigationGroupFontSize": 13` |
| `appearance.typography.heading1FontSize` | number | `28` | 12-72 | 12 / 72 | Page titles. | `"heading1FontSize": 28` |
| `appearance.typography.heading2FontSize` | number | `22` | 12-72 | 12 / 72 | Section titles. | `"heading2FontSize": 22` |
| `appearance.typography.heading3FontSize` | number | `18` | 12-72 | 12 / 72 | Sub-section titles. | `"heading3FontSize": 18` |
| `appearance.typography.statusBarFontSize` | number | `12` | 9-32 | 9 / 32 | Status bar text. | `"statusBarFontSize": 12` |
| `appearance.typography.buttonFontSize` | number | `14` | 9-32 | 9 / 32 | Button text. | `"buttonFontSize": 14` |
| `appearance.typography.normalFontWeight` | string | `Normal` | WPF font weight names | — | Font weight. | `"normalFontWeight": "Normal"` |
| `appearance.typography.mediumFontWeight` | string | `Medium` | WPF font weight names | — | Font weight. | `"mediumFontWeight": "Medium"` |
| `appearance.typography.headingFontWeight` | string | `SemiBold` | WPF font weight names | — | Font weight. | `"headingFontWeight": "SemiBold"` |
| `appearance.layout.navigationWidth` | number | `260` | 160-480 | 160 / 480 | Expanded navigation width. (device-independent pixels) | `"navigationWidth": 260` |
| `appearance.layout.navigationCollapsedWidth` | number | `64` | 40-120 | 40 / 120 | Collapsed navigation width. (device-independent pixels) | `"navigationCollapsedWidth": 64` |
| `appearance.layout.headerHeight` | number | `64` | 40-120 | 40 / 120 | Header height. (device-independent pixels) | `"headerHeight": 64` |
| `appearance.layout.statusBarHeight` | number | `28` | 20-48 | 20 / 48 | Status bar height. (device-independent pixels) | `"statusBarHeight": 28` |
| `appearance.layout.controlHeight` | number | `36` | 24-64 | 24 / 64 | Default control height. (device-independent pixels) | `"controlHeight": 36` |
| `appearance.layout.buttonMinHeight` | number | `36` | 24-64 | 24 / 64 | Minimum button height. (device-independent pixels) | `"buttonMinHeight": 36` |
| `appearance.layout.inputMinHeight` | number | `36` | 24-64 | 24 / 64 | Minimum input height. (device-independent pixels) | `"inputMinHeight": 36` |
| `appearance.layout.cornerRadius` | number | `4` | 0-16 | 0 / 16 | Default corner radius. (device-independent pixels) | `"cornerRadius": 4` |
| `appearance.layout.largeCornerRadius` | number | `8` | 0-32 | 0 / 32 | Corner radius of cards and dialogs. (device-independent pixels) | `"largeCornerRadius": 8` |
| `appearance.layout.spacingExtraSmall` | number | `4` | 0-48 | 0 / 48 | Spacing XS. (device-independent pixels) | `"spacingExtraSmall": 4` |
| `appearance.layout.spacingSmall` | number | `8` | 0-48 | 0 / 48 | Spacing S. (device-independent pixels) | `"spacingSmall": 8` |
| `appearance.layout.spacingMedium` | number | `12` | 0-48 | 0 / 48 | Spacing M. (device-independent pixels) | `"spacingMedium": 12` |
| `appearance.layout.spacingLarge` | number | `16` | 0-48 | 0 / 48 | Spacing L. (device-independent pixels) | `"spacingLarge": 16` |
| `appearance.layout.spacingExtraLarge` | number | `24` | 0-48 | 0 / 48 | Spacing XL. (device-independent pixels) | `"spacingExtraLarge": 24` |
| `appearance.light.colors.primary` / `appearance.dark.colors.primary` | string | `#002060` / `#72E1F5` | `#RRGGBB`, `#AARRGGBB` | — | Primary brand color (GDS navy). | `"primary": "#002060"` |
| `appearance.light.colors.primaryForeground` / `appearance.dark.colors.primaryForeground` | string | `#FFFFFF` / `#001433` | `#RRGGBB`, `#AARRGGBB` | — | Text and icons on primary. | `"primaryForeground": "#FFFFFF"` |
| `appearance.light.colors.secondary` / `appearance.dark.colors.secondary` | string | `#156082` / `#156082` | `#RRGGBB`, `#AARRGGBB` | — | Secondary brand color (GDS teal). | `"secondary": "#156082"` |
| `appearance.light.colors.secondaryForeground` / `appearance.dark.colors.secondaryForeground` | string | `#FFFFFF` / `#FFFFFF` | `#RRGGBB`, `#AARRGGBB` | — | Text and icons on secondary. | `"secondaryForeground": "#FFFFFF"` |
| `appearance.light.colors.accent` / `appearance.dark.colors.accent` | string | `#72E1F5` / `#AAEDF9` | `#RRGGBB`, `#AARRGGBB` | — | Accent color (GDS cyan). | `"accent": "#72E1F5"` |
| `appearance.light.colors.accentForeground` / `appearance.dark.colors.accentForeground` | string | `#002060` / `#001433` | `#RRGGBB`, `#AARRGGBB` | — | Text and icons on accent. | `"accentForeground": "#002060"` |
| `appearance.light.colors.applicationBackground` / `appearance.dark.colors.applicationBackground` | string | `#F3F6FA` / `#0A1426` | `#RRGGBB`, `#AARRGGBB` | — | Window background. | `"applicationBackground": "#F3F6FA"` |
| `appearance.light.colors.surfaceBackground` / `appearance.dark.colors.surfaceBackground` | string | `#FFFFFF` / `#111E36` | `#RRGGBB`, `#AARRGGBB` | — | Page and card background. | `"surfaceBackground": "#FFFFFF"` |
| `appearance.light.colors.surfaceSecondary` / `appearance.dark.colors.surfaceSecondary` | string | `#E4F2F5` / `#182A4A` | `#RRGGBB`, `#AARRGGBB` | — | Secondary surfaces such as panels and table headers. | `"surfaceSecondary": "#E4F2F5"` |
| `appearance.light.colors.navigationBackground` / `appearance.dark.colors.navigationBackground` | string | `#002060` / `#001433` | `#RRGGBB`, `#AARRGGBB` | — | Left navigation background. | `"navigationBackground": "#002060"` |
| `appearance.light.colors.navigationForeground` / `appearance.dark.colors.navigationForeground` | string | `#E6EDF7` / `#D3F1FC` | `#RRGGBB`, `#AARRGGBB` | — | Left navigation text. | `"navigationForeground": "#E6EDF7"` |
| `appearance.light.colors.navigationSelectedBackground` / `appearance.dark.colors.navigationSelectedBackground` | string | `#156082` / `#156082` | `#RRGGBB`, `#AARRGGBB` | — | Active navigation item background. | `"navigationSelectedBackground": "#156082"` |
| `appearance.light.colors.navigationSelectedForeground` / `appearance.dark.colors.navigationSelectedForeground` | string | `#FFFFFF` / `#FFFFFF` | `#RRGGBB`, `#AARRGGBB` | — | Active navigation item text. | `"navigationSelectedForeground": "#FFFFFF"` |
| `appearance.light.colors.navigationHoverBackground` / `appearance.dark.colors.navigationHoverBackground` | string | `#0F3A73` / `#0B2A5A` | `#RRGGBB`, `#AARRGGBB` | — | Navigation item background on hover. | `"navigationHoverBackground": "#0F3A73"` |
| `appearance.light.colors.headerBackground` / `appearance.dark.colors.headerBackground` | string | `#002060` / `#002060` | `#RRGGBB`, `#AARRGGBB` | — | Header background. | `"headerBackground": "#002060"` |
| `appearance.light.colors.headerForeground` / `appearance.dark.colors.headerForeground` | string | `#FFFFFF` / `#FFFFFF` | `#RRGGBB`, `#AARRGGBB` | — | Header text. | `"headerForeground": "#FFFFFF"` |
| `appearance.light.colors.statusBarBackground` / `appearance.dark.colors.statusBarBackground` | string | `#115472` / `#001433` | `#RRGGBB`, `#AARRGGBB` | — | Status bar background. | `"statusBarBackground": "#115472"` |
| `appearance.light.colors.statusBarForeground` / `appearance.dark.colors.statusBarForeground` | string | `#FFFFFF` / `#D3F1FC` | `#RRGGBB`, `#AARRGGBB` | — | Status bar text. | `"statusBarForeground": "#FFFFFF"` |
| `appearance.light.colors.textPrimary` / `appearance.dark.colors.textPrimary` | string | `#1F1F1F` / `#F2F5FA` | `#RRGGBB`, `#AARRGGBB` | — | Primary text. | `"textPrimary": "#1F1F1F"` |
| `appearance.light.colors.textSecondary` / `appearance.dark.colors.textSecondary` | string | `#4D4D4D` / `#B8C4D6` | `#RRGGBB`, `#AARRGGBB` | — | Secondary text. | `"textSecondary": "#4D4D4D"` |
| `appearance.light.colors.textDisabled` / `appearance.dark.colors.textDisabled` | string | `#7F7F7F` / `#6E7C93` | `#RRGGBB`, `#AARRGGBB` | — | Disabled text. | `"textDisabled": "#7F7F7F"` |
| `appearance.light.colors.border` / `appearance.dark.colors.border` | string | `#BFBFBF` / `#33476B` | `#RRGGBB`, `#AARRGGBB` | — | Control and card borders. | `"border": "#BFBFBF"` |
| `appearance.light.colors.divider` / `appearance.dark.colors.divider` | string | `#D9D9D9` / `#26385A` | `#RRGGBB`, `#AARRGGBB` | — | Dividers and grid lines. | `"divider": "#D9D9D9"` |
| `appearance.light.colors.inputBackground` / `appearance.dark.colors.inputBackground` | string | `#FFFFFF` / `#0D1830` | `#RRGGBB`, `#AARRGGBB` | — | Input background. | `"inputBackground": "#FFFFFF"` |
| `appearance.light.colors.inputForeground` / `appearance.dark.colors.inputForeground` | string | `#1F1F1F` / `#F2F5FA` | `#RRGGBB`, `#AARRGGBB` | — | Input text. | `"inputForeground": "#1F1F1F"` |
| `appearance.light.colors.inputBorder` / `appearance.dark.colors.inputBorder` | string | `#7F7F7F` / `#4A5F85` | `#RRGGBB`, `#AARRGGBB` | — | Input border. | `"inputBorder": "#7F7F7F"` |
| `appearance.light.colors.buttonBackground` / `appearance.dark.colors.buttonBackground` | string | `#156082` / `#156082` | `#RRGGBB`, `#AARRGGBB` | — | Button background. | `"buttonBackground": "#156082"` |
| `appearance.light.colors.buttonForeground` / `appearance.dark.colors.buttonForeground` | string | `#FFFFFF` / `#FFFFFF` | `#RRGGBB`, `#AARRGGBB` | — | Button text. | `"buttonForeground": "#FFFFFF"` |
| `appearance.light.colors.buttonHoverBackground` / `appearance.dark.colors.buttonHoverBackground` | string | `#115472` / `#1C77A0` | `#RRGGBB`, `#AARRGGBB` | — | Button background on hover. | `"buttonHoverBackground": "#115472"` |
| `appearance.light.colors.buttonPressedBackground` / `appearance.dark.colors.buttonPressedBackground` | string | `#002060` / `#115472` | `#RRGGBB`, `#AARRGGBB` | — | Button background while pressed. | `"buttonPressedBackground": "#002060"` |
| `appearance.light.colors.success` / `appearance.dark.colors.success` | string | `#107C10` / `#6CCB5F` | `#RRGGBB`, `#AARRGGBB` | — | Success status. | `"success": "#107C10"` |
| `appearance.light.colors.warning` / `appearance.dark.colors.warning` | string | `#9D5D00` / `#FCE100` | `#RRGGBB`, `#AARRGGBB` | — | Warning status. | `"warning": "#9D5D00"` |
| `appearance.light.colors.error` / `appearance.dark.colors.error` | string | `#C42B1C` / `#FF99A4` | `#RRGGBB`, `#AARRGGBB` | — | Error status. | `"error": "#C42B1C"` |
| `appearance.light.colors.information` / `appearance.dark.colors.information` | string | `#156082` / `#72E1F5` | `#RRGGBB`, `#AARRGGBB` | — | Information status. | `"information": "#156082"` |

## Example

```json
{
  "schemaVersion": 1,
  "appearance": {
    "theme": { "mode": "Dark", "allowUserSelection": true },
    "typography": { "baseFontSize": 15 },
    "dark": { "colors": { "navigationSelectedBackground": "#156082" } }
  }
}
```
