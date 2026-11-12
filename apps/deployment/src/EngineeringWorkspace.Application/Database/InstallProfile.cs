using System.Text.Json;
using System.Text.Json.Serialization;

namespace EngineeringWorkspace.Application.Database;

/// <summary>Installer choice (BLUEPRINT §10): what this computer may do with the shared database.</summary>
public enum InstallType
{
    /// <summary>No install profile (developer build or an install before 3.2): behaves like All-in-one.</summary>
    Unknown,

    /// <summary>Desktop app only; never creates, migrates or seeds the database.</summary>
    Workstation,

    /// <summary>Connects to SQL Server, creates the database if missing, applies migrations and seeds reference data.</summary>
    Server,

    /// <summary>Server and workstation on one computer (lab and test).</summary>
    AllInOne
}

/// <summary>
/// The install profile the installer writes to %ProgramData%\Global Data Systems\Secure Plus Agent\install.json: the install
/// type and the SQL Server and database entered (or passed with /SERVER= and /DATABASE=) during setup.
/// </summary>
public sealed record InstallProfile(InstallType Type, string? SqlServer, string? DatabaseName)
{
    public const string FileName = "install.json";
    public const string DefaultDatabaseName = "SecurePlus";

    public static InstallProfile None { get; } = new(InstallType.Unknown, null, null);

    /// <summary>Workstations never create, migrate, repair or seed the database.</summary>
    public bool CanProvision => Type != InstallType.Workstation;

    public string DisplayType => Type switch
    {
        InstallType.Workstation => "Workstation",
        InstallType.Server => "Server",
        InstallType.AllInOne => "All-in-one (lab/test)",
        _ => "Not recorded (before 3.2)"
    };

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Global Data Systems", "Secure Plus Agent", FileName);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Reads the profile; a missing or unreadable file gives <see cref="None"/>.</summary>
    public static InstallProfile Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return None;
            var profile = JsonSerializer.Deserialize<InstallProfile>(File.ReadAllText(path), Options);
            return profile is null ? None : profile with
            {
                SqlServer = string.IsNullOrWhiteSpace(profile.SqlServer) ? null : profile.SqlServer.Trim(),
                DatabaseName = string.IsNullOrWhiteSpace(profile.DatabaseName) ? null : profile.DatabaseName.Trim()
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return None;
        }
    }

    /// <summary>The refusal shown when a workstation is asked to change the database schema.</summary>
    public const string WorkstationRefusal =
        "This is a Workstation install: it never creates or upgrades the database. Run the Server (or All-in-one) install, or ask whoever maintains it, then try again.";
}
