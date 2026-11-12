using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Configuration.Appearance;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Desktop.Theming;

namespace EngineeringWorkspace.Desktop.Pages.Appearance;

public sealed record ColorSwatch(string Token, string Value);

/// <summary>Workspace → Settings → Appearance.</summary>
public sealed partial class AppearanceViewModel : PageViewModel
{
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _shell;
    private bool _updating;

    public AppearanceViewModel(IThemeService theme, IDialogService dialogs, IShellLauncher shell)
    {
        _theme = theme;
        _dialogs = dialogs;
        _shell = shell;
        Refresh();
    }

    public IReadOnlyList<ThemeMode> Modes => _theme.AvailableModes;

    /// <summary>Theme gallery: every built-in theme with a live preview card.</summary>
    public IReadOnlyList<ThemePreset> Presets => _theme.Presets;

    [ObservableProperty] private string _currentThemeText = string.Empty;

    [RelayCommand]
    private async Task ApplyPresetAsync(ThemePreset? preset)
    {
        await _theme.SetPresetAsync(preset?.Name);
        StatusMessage = preset is null ? "The workspace theme is in use again." : $"Theme '{preset.Name}' applied and remembered for you.";
        Refresh();
    }
    public ObservableCollection<AppearanceDiagnostic> Diagnostics { get; } = [];
    public ObservableCollection<ColorSwatch> Swatches { get; } = [];

    [ObservableProperty] private ThemeMode _selectedMode;
    [ObservableProperty] private ThemeMode _effectiveMode;
    [ObservableProperty] private bool _allowUserSelection;
    [ObservableProperty] private string _propertiesPath = string.Empty;
    [ObservableProperty] private string _sourceLabel = string.Empty;

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        Refresh();
    }

    partial void OnSelectedModeChanged(ThemeMode value)
    {
        if (!_updating) ChangeModeCommand.Execute(value);
    }

    [RelayCommand]
    private async Task ChangeModeAsync(ThemeMode mode)
    {
        if (!await _theme.SetThemeModeAsync(mode) && mode != _theme.CurrentMode)
            StatusMessage = "Theme selection is turned off (appearance.theme.allowUserSelection = false).";
        Refresh();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        var result = await _theme.ReloadPropertiesAsync();
        StatusMessage = result.Diagnostics.Count == 0 ? "Appearance reloaded with no problems." : $"Appearance reloaded with {result.Diagnostics.Count} diagnostic(s).";
        Refresh();
    }

    [RelayCommand]
    private void OpenPropertiesLocation()
    {
        var directory = Path.GetDirectoryName(PropertiesPath);
        if (directory is null || !_shell.OpenFolder(directory)) StatusMessage = $"The folder {directory} does not exist yet.";
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        if (!_dialogs.Confirm(new ConfirmationRequest("Reset to Built-In GDS Defaults",
                "The built-in GDS Light and Dark themes are applied for this session. The properties file is not deleted or changed; use Reload Appearance to apply it again.",
                [new("Properties file", PropertiesPath)], "Apply defaults")))
            return;
        await _theme.UseBuiltInDefaultsAsync();
        StatusMessage = "Built-in GDS defaults applied for this session.";
        Refresh();
    }

    [RelayCommand]
    private async Task CreatePropertiesFileAsync()
    {
        var exists = File.Exists(PropertiesPath);
        if (exists && !_dialogs.Confirm(new ConfirmationRequest("Overwrite ApplicationProperties.json",
                "The existing file will be replaced with the complete built-in GDS defaults (standard JSON, no comments). Your current customizations in it will be lost.",
                [new("File", PropertiesPath)], "Overwrite file", IsDestructive: true)))
            return;
        var result = await Task.Run(() => ApplicationPropertiesLoader.WriteDefaults(PropertiesPath, overwrite: exists));
        StatusMessage = result.Succeeded ? $"Created {PropertiesPath} from the built-in defaults. Use Reload Appearance to apply it." : result.Error!.ToString();
    }

    private void Refresh()
    {
        CurrentThemeText = _theme.CurrentPresetName is { } preset ? $"Current theme: {preset}" : "Current theme: the workspace theme (properties file or stored workspace theme)";
        _updating = true;
        SelectedMode = _theme.CurrentMode;
        _updating = false;
        EffectiveMode = _theme.EffectiveMode;
        AllowUserSelection = _theme.AllowUserSelection;
        PropertiesPath = _theme.Properties.PropertiesPath;
        SourceLabel = _theme.UsingBuiltInDefaultsOverride || _theme.Properties.Source == AppearanceSource.BuiltInDefaults
            ? "Built-In GDS Default"
            : "Custom ApplicationProperties.json";
        Diagnostics.Clear();
        foreach (var diagnostic in _theme.Properties.Diagnostics) Diagnostics.Add(diagnostic);
        Swatches.Clear();
        foreach (var (token, value) in _theme.GetEffectiveTheme().Colors.Tokens) Swatches.Add(new ColorSwatch(token, value));
    }
}
