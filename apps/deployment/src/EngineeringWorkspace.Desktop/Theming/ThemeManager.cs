using System.Windows;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Configuration.Appearance;
using EngineeringWorkspace.Domain.Auditing;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EngineeringWorkspace.Desktop.Theming;

/// <summary>
/// The single theme service. Precedence: built-in GDS defaults &lt; ApplicationProperties.json &lt; runtime mode
/// selection. Replaces one theme dictionary in Application resources so DynamicResource references update live.
/// System mode follows the Windows app theme (HKCU AppsUseLightTheme) and reacts to Windows theme changes.
/// </summary>
public sealed class ThemeManager : IThemeService, IDisposable
{
    private const string ThemeDictionaryMarker = "EngineeringWorkspace.Theme";
    private readonly string _propertiesPath;
    private readonly IAuditService _audit;
    private readonly ILogger<ThemeManager> _logger;
    private AppearanceLoadResult _properties;
    private ThemeMode _mode;

    private readonly Application.State.IApplicationStateStore? _state;
    private ThemePreset? _preset;

    public ThemeManager(string propertiesPath, IAuditService audit, ILogger<ThemeManager> logger, Application.State.IApplicationStateStore? state = null)
    {
        _state = state;
        _propertiesPath = propertiesPath;
        _audit = audit;
        _logger = logger;
        _properties = ApplicationPropertiesLoader.Load(propertiesPath);
        _mode = _properties.Properties.Appearance.Theme.Mode;
        LogDiagnostics(_properties);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public ThemeMode CurrentMode => _mode;
    public ThemeMode EffectiveMode
    {
        get
        {
            var mode = _preset?.Mode ?? _mode;
            return mode == ThemeMode.System ? (IsWindowsAppThemeDark() ? ThemeMode.Dark : ThemeMode.Light) : mode;
        }
    }

    public IReadOnlyList<ThemePreset> Presets => ThemePresets.All;
    public string? CurrentPresetName => _preset?.Name;

    public void RestorePreset(string? name)
    {
        _preset = ThemePresets.Find(name);
        ApplyTheme();
    }

    public async Task<bool> SetPresetAsync(string? name, CancellationToken cancellationToken = default)
    {
        var preset = ThemePresets.Find(name);
        if (preset?.Name == _preset?.Name) return false;
        _preset = preset;
        if (_state is not null)
        {
            var state = await _state.LoadAsync(cancellationToken);
            await _state.SaveAsync(state with { ThemePreset = preset?.Name }, cancellationToken);
        }

        await ApplyAndAnnounceAsync(preset is null ? "Workspace theme restored." : $"Theme '{preset.Name}' applied.", cancellationToken);
        return true;
    }

    /// <summary>The appearance to render: the properties (file, workspace theme) with the built-in theme's colors when one is chosen.</summary>
    private AppearanceProperties EffectiveAppearance =>
        _preset is null
            ? _properties.Properties.Appearance
            : _properties.Properties.Appearance with { Light = new ThemeVariantProperties(_preset.Light), Dark = new ThemeVariantProperties(_preset.Dark) };
    public IReadOnlyList<ThemeMode> AvailableModes { get; } = [ThemeMode.Light, ThemeMode.Dark, ThemeMode.System];
    public bool AllowUserSelection => _properties.Properties.Appearance.Theme.AllowUserSelection;
    public AppearanceLoadResult Properties => _properties;
    public bool UsingBuiltInDefaultsOverride { get; private set; }
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public void ApplyTheme()
    {
        var resources = System.Windows.Application.Current.Resources;
        var dictionary = ThemeResourceBuilder.Build(EffectiveAppearance, EffectiveMode);
        dictionary[ThemeDictionaryMarker] = true;
        var existing = resources.MergedDictionaries.FirstOrDefault(candidate => candidate.Contains(ThemeDictionaryMarker));
        if (existing is null) resources.MergedDictionaries.Insert(0, dictionary);
        else resources.MergedDictionaries[resources.MergedDictionaries.IndexOf(existing)] = dictionary;
    }

    public async Task<bool> SetThemeModeAsync(ThemeMode mode, CancellationToken cancellationToken = default)
    {
        if (!AllowUserSelection || mode == _mode || !Enum.IsDefined(mode)) return false;
        var previous = _mode;
        _mode = mode;
        await ApplyAndAnnounceAsync($"Theme changed from {previous} to {mode}.", cancellationToken);
        return true;
    }

    public async Task<AppearanceLoadResult> ReloadPropertiesAsync(CancellationToken cancellationToken = default)
    {
        _properties = await Task.Run(() => ApplicationPropertiesLoader.Load(_propertiesPath), cancellationToken);
        UsingBuiltInDefaultsOverride = false;
        _mode = _properties.Properties.Appearance.Theme.Mode;
        LogDiagnostics(_properties);
        await ApplyAndAnnounceAsync("Appearance properties reloaded.", cancellationToken);
        return _properties;
    }

    public async Task UseBuiltInDefaultsAsync(CancellationToken cancellationToken = default)
    {
        _properties = new AppearanceLoadResult(ApplicationProperties.BuiltInDefaults, _propertiesPath, AppearanceSource.BuiltInDefaults,
            [new(Application.Configuration.ConfigurationSeverity.Information, string.Empty, null, "Built-in GDS defaults applied for this session; the properties file was not changed.", null)]);
        UsingBuiltInDefaultsOverride = true;
        _mode = _properties.Properties.Appearance.Theme.Mode;
        await ApplyAndAnnounceAsync("Built-in GDS appearance defaults applied.", cancellationToken);
    }

    public ThemeVariantProperties GetEffectiveTheme() => EffectiveAppearance.For(EffectiveMode);

    public string? CurrentThemeName { get; private set; }

    public async Task UseThemeAsync(string name, string propertiesJson, CancellationToken cancellationToken = default)
    {
        if (CurrentThemeName == name && _properties.Source == AppearanceSource.CustomFile) return;
        _properties = ApplicationPropertiesLoader.Parse(propertiesJson, $"theme '{name}'");
        UsingBuiltInDefaultsOverride = false;
        CurrentThemeName = name;
        _mode = _properties.Properties.Appearance.Theme.Mode;
        LogDiagnostics(_properties);
        await ApplyAndAnnounceAsync($"Theme '{name}' applied.", cancellationToken);
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private async Task ApplyAndAnnounceAsync(string summary, CancellationToken cancellationToken)
    {
        ApplyTheme();
        _logger.LogInformation("Theme applied: selected {Mode}, effective {EffectiveMode} ({Summary})", _mode, EffectiveMode, summary);
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(_mode, EffectiveMode, summary));
        await _audit.RecordAsync(AuditEventTypes.ThemeChanged, AuditOutcome.Success, summary,
            new Dictionary<string, string?> { ["Mode"] = _mode.ToString(), ["EffectiveMode"] = EffectiveMode.ToString(), ["Source"] = _properties.Source.ToString() }, cancellationToken);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || (_preset?.Mode ?? _mode) != ThemeMode.System) return;
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ApplyTheme();
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(_mode, EffectiveMode, "Windows app theme changed."));
        });
    }

    private void LogDiagnostics(AppearanceLoadResult result)
    {
        foreach (var diagnostic in result.Diagnostics)
            _logger.Log(diagnostic.Severity == Application.Configuration.ConfigurationSeverity.Warning ? LogLevel.Warning : LogLevel.Information,
                "Appearance diagnostic: {Diagnostic}", diagnostic.ToString());
    }

    private static bool IsWindowsAppThemeDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
