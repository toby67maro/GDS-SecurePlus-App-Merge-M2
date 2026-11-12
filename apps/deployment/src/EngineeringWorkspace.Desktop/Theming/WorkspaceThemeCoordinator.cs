using System.IO;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Themes;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Configuration.Appearance;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Desktop.Theming;

/// <summary>
/// Applies the theme of the open workspace (or the shared default theme) whenever the workspace changes, so switching
/// the workspace at the top of the window switches the theme. Seeds the GDS system themes and imports this computer's
/// ApplicationProperties.json as a theme once.
/// </summary>
public sealed class WorkspaceThemeCoordinator(
    IWorkspaceService workspaces,
    IThemeRepository themes,
    IThemeService theme,
    ISharedSettingsStore settings,
    IConfigurationDiagnostics configuration,
    Application.Settings.PathSettings paths,
    AuditContext audit,
    ILogger<WorkspaceThemeCoordinator> logger)
{
    private string? _defaultThemeKey;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        workspaces.WorkspaceChanged += (_, change) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => ApplyAsync(change.Current?.ThemeKey));
        _defaultThemeKey = configuration.Entries.FirstOrDefault(entry => string.Equals(entry.Key, ThemeKeys.DefaultThemeSetting, StringComparison.OrdinalIgnoreCase))?.EffectiveValue;
        if (!themes.IsAvailable) return;
        try
        {
            await EnsureSystemThemesAsync(cancellationToken);
            await ImportLocalAppearanceAsync(cancellationToken);
            await ApplyAsync(workspaces.CurrentWorkspace?.ThemeKey);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            logger.LogWarning(exception, "Themes could not be prepared; the local appearance stays in use");
        }
    }

    /// <summary>The default theme key changes take effect immediately on this computer.</summary>
    public void SetDefaultTheme(string? themeKey) => _defaultThemeKey = string.IsNullOrWhiteSpace(themeKey) ? null : themeKey;

    public async Task ApplyAsync(string? workspaceThemeKey)
    {
        if (!themes.IsAvailable) return;
        var key = workspaceThemeKey ?? _defaultThemeKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        try
        {
            var record = await themes.GetThemeAsync(key);
            if (record is null)
            {
                logger.LogWarning("Theme {ThemeKey} does not exist; the current appearance stays in use", key);
                return;
            }

            await theme.UseThemeAsync(record.Name, record.PropertiesJson);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            logger.LogWarning(exception, "Theme {ThemeKey} could not be applied", key);
        }
    }

    private async Task EnsureSystemThemesAsync(CancellationToken cancellationToken)
    {
        var existing = (await themes.ListThemesAsync(cancellationToken)).Select(item => item.ThemeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaults = ApplicationProperties.BuiltInDefaults.Appearance;
        foreach (var (key, name, mode, description) in new[]
                 {
                     (ThemeKeys.GdsLight, "GDS Light", ThemeMode.Light, "Global Data Systems colours, light."),
                     (ThemeKeys.GdsDark, "GDS Dark", ThemeMode.Dark, "Global Data Systems colours, dark."),
                     (ThemeKeys.GdsSystem, "GDS (Follow Windows)", ThemeMode.System, "Light or dark, following the Windows app theme.")
                 })
        {
            if (existing.Contains(key)) continue;
            var json = ApplicationPropertiesLoader.Serialize(mode, true, defaults.Light.Colors.Tokens, defaults.Dark.Colors.Tokens);
            await themes.SaveThemeAsync(new ThemeRecord(key, name, description, json, true, "Secure Plus", DateTimeOffset.UtcNow, null, null), cancellationToken);
        }
    }

    /// <summary>A customised ApplicationProperties.json on this computer becomes a shared theme (once) and moves to the backup folder.</summary>
    private async Task ImportLocalAppearanceAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.ConfigurationDirectory, ApplicationPropertiesLoader.FileName);
        var property = ThemeKeys.ImportedPropertyPrefix + settings.ComputerName;
        if (!settings.IsAvailable || (await settings.GetPropertiesAsync(cancellationToken)).ContainsKey(property)) return;
        string? imported = null;
        if (File.Exists(path))
        {
            var loaded = ApplicationPropertiesLoader.Load(path);
            var appearance = loaded.Properties.Appearance;
            var builtIn = ApplicationProperties.BuiltInDefaults.Appearance;
            var customised = loaded.Source == AppearanceSource.CustomFile &&
                             (appearance.Theme.Mode != builtIn.Theme.Mode || !appearance.Light.Colors.Tokens.OrderBy(pair => pair.Key).SequenceEqual(builtIn.Light.Colors.Tokens.OrderBy(pair => pair.Key)) ||
                              !appearance.Dark.Colors.Tokens.OrderBy(pair => pair.Key).SequenceEqual(builtIn.Dark.Colors.Tokens.OrderBy(pair => pair.Key)));
            if (customised)
            {
                imported = $"IMPORTED-{settings.ComputerName}".ToUpperInvariant();
                await themes.SaveThemeAsync(new ThemeRecord(imported, $"{settings.ComputerName} Appearance", $"Imported from {path}.",
                    ApplicationPropertiesLoader.Serialize(appearance.Theme.Mode, appearance.Theme.AllowUserSelection, appearance.Light.Colors.Tokens, appearance.Dark.Colors.Tokens),
                    false, audit.Actor, DateTimeOffset.UtcNow, null, null), cancellationToken);
                if (_defaultThemeKey is null)
                {
                    await settings.WriteAsync([new SettingWrite(SettingScopes.Shared, ThemeKeys.DefaultThemeSetting, imported)], audit.Actor, cancellationToken);
                    _defaultThemeKey = imported;
                }
            }
        }

        await settings.SetPropertyAsync(property, imported ?? "nothing to import", audit.Actor, cancellationToken);
    }
}
