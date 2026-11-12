namespace EngineeringWorkspace.Application.Reference;

/// <summary>One industry on the customer pick-list (secureplus.Industry). Replaces NAICS for Secure Plus (4.0).</summary>
public sealed record IndustryValue(string Code, string Name, int SortOrder, bool IsActive = true, bool IsSystem = true, bool IsModified = false);

/// <summary>
/// The Industry pick-list: seeded with the GDS list (inserted only when missing, never overwriting a Lead's edits) and editable by Leads.
/// "Other (specify)" lets the customer profile hold free text.
/// </summary>
public static class IndustryList
{
    public const string Other = "OTHER";

    public static IReadOnlyList<IndustryValue> Defaults { get; } =
    [
        new("HEALTHCARE", "Healthcare & Medical", 10),
        new("OIL-GAS-ENERGY", "Oil & Gas / Energy", 20),
        new("MARINE", "Marine & Maritime", 30),
        new("MANUFACTURING", "Manufacturing & Industrial", 40),
        new("CONSTRUCTION", "Construction & Engineering", 50),
        new("TRANSPORTATION", "Transportation & Logistics", 60),
        new("FINANCIAL", "Financial Services & Banking", 70),
        new("INSURANCE", "Insurance", 80),
        new("LEGAL", "Legal", 90),
        new("ACCOUNTING", "Accounting & Professional Services", 100),
        new("GOVERNMENT", "Government & Public Sector", 110),
        new("EDUCATION", "Education", 120),
        new("NONPROFIT", "Non-Profit", 130),
        new("RETAIL", "Retail & Hospitality", 140),
        new("REAL-ESTATE", "Real Estate & Property Management", 150),
        new("TECHNOLOGY", "Technology & Telecommunications", 160),
        new("AGRICULTURE", "Agriculture", 170),
        new("UTILITIES", "Utilities", 180),
        new("DEFENSE", "Defense & Aerospace", 190),
        new(Other, "Other (specify)", 200),
    ];

    /// <summary>The display text of a customer's industry: the list name, or the free text for Other.</summary>
    public static string Display(IReadOnlyList<IndustryValue> list, string? code, string? other) =>
        code is null ? string.Empty
        : code == Other ? (string.IsNullOrWhiteSpace(other) ? "Other" : other.Trim())
        : list.FirstOrDefault(item => item.Code == code)?.Name ?? code;
}

public interface IIndustryRepository
{
    bool IsAvailable { get; }

    /// <summary>The list (the built-in defaults when the database is not ready).</summary>
    Task<IReadOnlyList<IndustryValue>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>Inserts built-in values that are missing; never changes existing rows.</summary>
    Task<int> EnsureSeededAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds or edits a value (Lead only once users are defined); an edited built-in is marked modified so seeds leave it alone.</summary>
    Task SaveAsync(IndustryValue value, CancellationToken cancellationToken = default);
}
