using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Application.Customers;

/// <summary>A customer profile as stored in the database, with its version and who last changed it.</summary>
public sealed record StoredCustomerProfile(CustomerProfile Profile, int Version, bool IsSample, string ModifiedBy, DateTimeOffset ModifiedAtUtc);

/// <summary>
/// The database as the system of record for customer profiles (secureplus.CustomerProfile, projected into cust.Customer,
/// cust.Contact, cust.Tenant, cust.Engagement and audit.TenantConnection). Shared by every computer using the database.
/// </summary>
public interface ICustomerProfileRepository
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<StoredCustomerProfile>> ListCustomerProfilesAsync(CancellationToken cancellationToken = default);
    Task<StoredCustomerProfile?> LoadCustomerProfileAsync(string customerKey, CancellationToken cancellationToken = default);

    /// <summary>Saves the profile; <paramref name="modifiedAtUtc"/> keeps the original time when importing older files.</summary>
    Task<StoredCustomerProfile> SaveCustomerProfileAsync(CustomerProfile profile, bool isSample, DateTimeOffset? modifiedAtUtc = null, CancellationToken cancellationToken = default);

    Task ArchiveCustomerProfileAsync(string customerKey, CancellationToken cancellationToken = default);

    /// <summary>Records the licences seen in a tenant (kb.LicenseSku and cust.TenantLicense for <paramref name="asOf"/>).</summary>
    Task RecordTenantLicensesAsync(string customerKey, string tenantKey, IReadOnlyList<TenantLicenseCount> licenses, DateOnly asOf, CancellationToken cancellationToken = default);

    /// <summary>Records a tenant permission check (audit.TenantConnection consent status and time).</summary>
    Task RecordTenantVerificationAsync(string customerKey, string tenantKey, string consentStatusCode, CancellationToken cancellationToken = default);
}

/// <summary>One licence SKU of a tenant: purchased (enabled prepaid units) and assigned (consumed units).</summary>
public sealed record TenantLicenseCount(string SkuPartNumber, int Quantity, int Assigned);

public static class TenantLicenses
{
    /// <summary>Reads Microsoft Graph subscribedSkus items (skuPartNumber, prepaidUnits.enabled, consumedUnits).</summary>
    public static IReadOnlyList<TenantLicenseCount> FromSubscribedSkus(IEnumerable<System.Text.Json.JsonElement> items)
    {
        var licenses = new List<TenantLicenseCount>();
        foreach (var item in items)
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object || !item.TryGetProperty("skuPartNumber", out var sku) || sku.GetString() is not { Length: > 0 } part) continue;
            var quantity = item.TryGetProperty("prepaidUnits", out var prepaid) && prepaid.TryGetProperty("enabled", out var enabled) && enabled.TryGetInt32(out var count) ? count : 0;
            var assigned = item.TryGetProperty("consumedUnits", out var consumed) && consumed.TryGetInt32(out var used) ? used : 0;
            licenses.Add(new TenantLicenseCount(part, quantity, assigned));
        }

        return licenses.GroupBy(license => license.SkuPartNumber, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TenantLicenseCount(group.Key, group.Sum(license => license.Quantity), group.Sum(license => license.Assigned))).ToArray();
    }
}
