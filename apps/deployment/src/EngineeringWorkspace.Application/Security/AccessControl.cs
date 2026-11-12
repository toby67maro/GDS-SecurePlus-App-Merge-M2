namespace EngineeringWorkspace.Application.Security;

/// <summary>
/// Secure Plus roles (BLUEPRINT §9). Each is assigned per user by a Lead in Settings > Users &amp; Roles and maps to a
/// database role (secureplus_*) created by migration V0008.
/// </summary>
public static class AppRoles
{
    public const string Sales = nameof(Sales);
    public const string Engineer = nameof(Engineer);
    public const string Technician = nameof(Technician);
    public const string Noc = "NOC";
    public const string Lead = nameof(Lead);
    public const string Manager = nameof(Manager);

    public static IReadOnlyList<string> All { get; } = [Sales, Engineer, Technician, Noc, Lead, Manager];

    public static string DisplayName(string role) => role switch
    {
        Technician => "Technician / Deployer",
        Noc => "NOC / Help Desk",
        Lead => "Lead / Architect",
        Manager => "Manager / Executive",
        _ => role
    };

    public static bool IsValid(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);

    /// <summary>The database role a user of <paramref name="role"/> is a member of.</summary>
    public static string DatabaseRole(string role) => IsValid(role)
        ? "secureplus_" + (role == Technician ? "technician" : role.ToLowerInvariant())
        : throw new ArgumentException($"Unknown role '{role}'.", nameof(role));

    /// <summary>Engineer, Technician/Deployer and Lead may read tenant credentials (run assessments and deployments) and hold the app key.</summary>
    public static bool CanUseTenantCredentials(string? role) => role is Engineer or Technician or Lead;

    /// <summary>Sales, Lead and Manager may see rates.</summary>
    public static bool CanViewRates(string? role) => role is Sales or Lead or Manager;

    public static bool CanManageUsers(string? role) => role is Lead;
}

/// <summary>One person allowed to use Secure Plus.</summary>
public sealed record AppUser(string WindowsAccount, string? Upn, string? DisplayName, string RoleCode, bool IsActive, DateTimeOffset? LastSeenUtc = null)
{
    /// <summary>Normalises "gds\tblanchard" to "GDS\tblanchard" style: domain upper case, name as typed; trims spaces.</summary>
    public static string NormalizeAccount(string account)
    {
        var value = (account ?? string.Empty).Trim();
        var slash = value.IndexOf('\\', StringComparison.Ordinal);
        return slash > 0 ? value[..slash].ToUpperInvariant() + value[slash..] : value;
    }

    /// <summary>A Windows account must be DOMAIN\name (no spaces, brackets or quotes, so it is safe in generated T-SQL).</summary>
    public static bool IsValidAccount(string account) =>
        System.Text.RegularExpressions.Regex.IsMatch(account ?? string.Empty, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,62}\\[A-Za-z0-9][A-Za-z0-9._$-]{0,103}$");
}

/// <summary>Result of granting a user database access.</summary>
public sealed record DatabaseAccessResult(bool Applied, string Message, string Script);

/// <summary>Users &amp; Roles: who may use the application and with which role.</summary>
public interface IUserDirectory
{
    /// <summary>The Windows account of the person running the application (DOMAIN\name).</summary>
    string CurrentAccount { get; }

    /// <summary>True when at least one user is defined; until then every user has full access (as in 3.1).</summary>
    Task<bool> IsEnforcedAsync(CancellationToken cancellationToken = default);

    /// <summary>The current user's entry, or null when users are not defined or the user is not listed.</summary>
    Task<AppUser?> CurrentAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds or updates a user (Lead only once users are defined; the first user must be a Lead).</summary>
    Task SaveAsync(AppUser user, CancellationToken cancellationToken = default);

    /// <summary>Creates the SQL Server login and database user if needed and sets the role membership; returns the script when it lacks permission.</summary>
    Task<DatabaseAccessResult> ApplyDatabaseAccessAsync(AppUser user, CancellationToken cancellationToken = default);

    /// <summary>The T-SQL a DBA can run to grant (or, for inactive users, remove) the user's database access.</summary>
    string DatabaseAccessScript(AppUser user);
}

/// <summary>The designated scheduling PC (BLUEPRINT §10.1).</summary>
public sealed record SchedulerHostStatus(string? ComputerName, string? DesignatedBy, DateTimeOffset? DesignatedAtUtc, DateTimeOffset? HeartbeatUtc,
    DateTimeOffset? LastRunUtc, string? LastStatus, string? LastMessage, string? AppVersion)
{
    /// <summary>A heartbeat older than this means the scheduling PC has stopped.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public bool IsDesignated => !string.IsNullOrWhiteSpace(ComputerName);

    public bool IsStale(DateTimeOffset now) => IsDesignated && (HeartbeatUtc is null || now - HeartbeatUtc.Value > StaleAfter);
}

public interface ISchedulerHostService
{
    string ThisComputer { get; }
    Task<SchedulerHostStatus?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks <paramref name="computerName"/> (or none when null) as the only computer that runs schedules.</summary>
    Task DesignateAsync(string? computerName, CancellationToken cancellationToken = default);

    /// <summary>True when this computer may run due schedules: it is the designated PC, or no PC is designated.</summary>
    Task<bool> MayRunSchedulesAsync(CancellationToken cancellationToken = default);

    /// <summary>Records that this computer checked for due schedules (only when it is the designated PC).</summary>
    Task HeartbeatAsync(string? status, string? message, bool ranSchedules, CancellationToken cancellationToken = default);
}
