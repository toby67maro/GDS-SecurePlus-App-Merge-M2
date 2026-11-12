namespace EngineeringWorkspace.Application.Themes;

/// <summary>A named theme in secureplus.Theme: appearance JSON (schema 1 of ApplicationProperties.json).</summary>
public sealed record ThemeRecord(
    string ThemeKey,
    string Name,
    string? Description,
    string PropertiesJson,
    bool IsSystem,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    string? ModifiedBy,
    DateTimeOffset? ModifiedAtUtc);

/// <summary>Themes shared by every computer using the database.</summary>
public interface IThemeRepository
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<ThemeRecord>> ListThemesAsync(CancellationToken cancellationToken = default);
    Task<ThemeRecord?> GetThemeAsync(string themeKey, CancellationToken cancellationToken = default);
    Task SaveThemeAsync(ThemeRecord theme, CancellationToken cancellationToken = default);
    Task DeleteThemeAsync(string themeKey, CancellationToken cancellationToken = default);
}

public static class ThemeKeys
{
    /// <summary>Shared setting holding the theme used by workspaces without their own theme.</summary>
    public const string DefaultThemeSetting = "Appearance:DefaultTheme";

    public const string GdsLight = "GDS-LIGHT";
    public const string GdsDark = "GDS-DARK";
    public const string GdsSystem = "GDS-SYSTEM";

    /// <summary>Database property recording that a computer's ApplicationProperties.json was imported as a theme.</summary>
    public const string ImportedPropertyPrefix = "ThemeImported:";
}
