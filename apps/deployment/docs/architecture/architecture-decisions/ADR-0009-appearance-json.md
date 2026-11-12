# ADR-0009: Appearance in ApplicationProperties.json, behaviour in INI

- Status: Accepted
- Date: 2026-11-06

## Decision

Theme mode, user selection, colors, typography and layout live in `ApplicationProperties.json` (schema version 1,
camelCase, all optional except `schemaVersion`). Branding identity stays in `Branding.ini`. Defaults and ranges are
defined once in `AppearanceSchema`. Appearance problems never block startup: malformed files are ignored, invalid
values fall back individually, unknown properties are ignored, all with diagnostics. The existing `ThemeManager` is
the single theme service behind the WPF-free `IThemeService`.

## Consequences

`DefaultTheme`, `AllowThemeSelection` and `[Branding.Colors]` in `Branding.ini` are ignored with a warning.
