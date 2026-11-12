using EngineeringWorkspace.Configuration.Appearance;

namespace EngineeringWorkspace.Desktop.Theming;

public sealed record ThemeChangedEventArgs(ThemeMode SelectedMode, ThemeMode EffectiveMode, string Reason);

/// <summary>
/// The single theme service (WPF-free contract). Configuration reads and validates ApplicationProperties.json;
/// the implementation converts the effective properties into WPF resources that every view uses through
/// DynamicResource, so changes apply live without per-ViewModel handling.
/// </summary>
public interface IThemeService
{
    /// <summary>The selected mode: Light, Dark or System.</summary>
    ThemeMode CurrentMode { get; }

    /// <summary>The applied variant: Light or Dark (System resolved through the Windows app theme).</summary>
    ThemeMode EffectiveMode { get; }

    IReadOnlyList<ThemeMode> AvailableModes { get; }
    bool AllowUserSelection { get; }
    AppearanceLoadResult Properties { get; }
    bool UsingBuiltInDefaultsOverride { get; }

    /// <summary>Converts the effective properties into WPF resources and applies them.</summary>
    void ApplyTheme();

    /// <summary>Changes the mode for this session only (no file is rewritten). Ignored unless user selection is allowed.</summary>
    Task<bool> SetThemeModeAsync(ThemeMode mode, CancellationToken cancellationToken = default);

    /// <summary>Re-reads, validates, resolves and applies ApplicationProperties.json.</summary>
    Task<AppearanceLoadResult> ReloadPropertiesAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies the built-in GDS defaults for this session without touching the file.</summary>
    Task UseBuiltInDefaultsAsync(CancellationToken cancellationToken = default);

    ThemeVariantProperties GetEffectiveTheme();

    /// <summary>Name of the stored theme in use (from the workspace or the default theme), or null for the built-in/local appearance.</summary>
    string? CurrentThemeName { get; }

    /// <summary>Applies a stored theme (appearance JSON from secureplus.Theme) for this session.</summary>
    Task UseThemeAsync(string name, string propertiesJson, CancellationToken cancellationToken = default);

    /// <summary>The built-in themes (Light, Medium, Dark, System, GDS, Aurora, Clay, Blueprint and many more).</summary>
    IReadOnlyList<ThemePreset> Presets { get; }

    /// <summary>The built-in theme in use, or null when the workspace / properties-file theme is used.</summary>
    string? CurrentPresetName { get; }

    /// <summary>Applies a built-in theme and remembers it for this user; null returns to the workspace / properties-file theme.</summary>
    Task<bool> SetPresetAsync(string? name, CancellationToken cancellationToken = default);

    /// <summary>Restores the remembered built-in theme at startup (no audit event).</summary>
    void RestorePreset(string? name);

    event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
}
