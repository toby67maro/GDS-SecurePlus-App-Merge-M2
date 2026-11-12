namespace EngineeringWorkspace.Application.Security;

/// <summary>Whether this computer can read the secrets shared in the database.</summary>
public enum OrganizationKeyState
{
    /// <summary>No database, or the database is not ready.</summary>
    Unavailable,

    /// <summary>The database has no organization key yet: an administrator creates it once.</summary>
    NotCreated,

    /// <summary>The database has a key but this computer does not: enter it (from the administrator).</summary>
    NotOnThisComputer,

    /// <summary>This computer holds the key; shared secrets can be read and written.</summary>
    Ready
}

/// <summary>
/// The organization key (AES-256) that encrypts tenant secrets stored in the SecurePlus database. The key is never
/// stored in the database: each computer keeps its copy protected by Windows DPAPI, and the database holds only a key
/// ID and a check value to verify an entered key.
/// </summary>
public interface IOrganizationKeyService
{
    Task<OrganizationKeyState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the key (only when none exists) and returns it as text to hand to other computers. Shown once.</summary>
    Task<string> CreateAsync(CancellationToken cancellationToken = default);

    /// <summary>Verifies <paramref name="keyText"/> against the database and keeps it on this computer. Returns an error message or null.</summary>
    Task<string?> EnrolAsync(string keyText, CancellationToken cancellationToken = default);

    /// <summary>The key as text, for an administrator to give to another computer (only when this computer holds it).</summary>
    Task<string?> ExportAsync(CancellationToken cancellationToken = default);

    string? KeyId { get; }
}
