using System.Data;
using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Customer profiles as the system of record: the full profile is a JSON document in secureplus.CustomerProfile, and
/// every save projects it into the EaaS v3 tables cust.Customer, cust.Contact, cust.Tenant, cust.Engagement and
/// audit.TenantConnection in the same transaction, so reports and other EaaS components see complete customer data.
/// </summary>
public sealed partial class SqlEngagementRepository : ICustomerProfileRepository
{
    public async Task<IReadOnlyList<StoredCustomerProfile>> ListCustomerProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null,
            $"SELECT CustomerKey, ProfileJson, Version, IsSample, ModifiedBy, ModifiedAtUtc FROM {session.Table("secureplus", "CustomerProfile")} WHERE IsArchived = 0 ORDER BY Name;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var profiles = new List<StoredCustomerProfile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            try
            {
                profiles.Add(new StoredCustomerProfile(CustomerProfileJson.Deserialize(key, reader.GetString(1)), Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture), reader.GetString(4), SqlSession.ReadDate(reader.GetValue(5))));
            }
            catch (System.Text.Json.JsonException)
            {
                // A damaged document is skipped rather than hiding every other customer; the row stays for repair.
            }
        }

        return profiles;
    }

    public async Task<StoredCustomerProfile?> LoadCustomerProfileAsync(string customerKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null,
            $"SELECT ProfileJson, Version, IsSample, ModifiedBy, ModifiedAtUtc FROM {session.Table("secureplus", "CustomerProfile")} WHERE CustomerKey = @key;");
        DatabaseConnectionFactory.Parameter(command, "@key", customerKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new StoredCustomerProfile(CustomerProfileJson.Deserialize(customerKey, reader.GetString(0)), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture), reader.GetString(3), SqlSession.ReadDate(reader.GetValue(4)));
    }

    public async Task<StoredCustomerProfile> SaveCustomerProfileAsync(CustomerProfile profile, bool isSample, DateTimeOffset? modifiedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var now = modifiedAtUtc ?? timeProvider.GetUtcNow();
        var table = session.Table("secureplus", "CustomerProfile");
        var version = await ScalarIntAsync(session, transaction, $"SELECT Version FROM {table} WHERE CustomerKey = @key;", profile.Id.Value, cancellationToken);
        var json = CustomerProfileJson.Serialize(profile);
        var sql = version is null
            ? $"INSERT INTO {table} (CustomerKey, Name, ProfileJson, Version, IsSample, IsArchived, CreatedBy, CreatedAtUtc, ModifiedBy, ModifiedAtUtc) " +
              "VALUES (@key, @name, @json, 1, @sample, @archived, @by, @now, @by, @now);"
            : $"UPDATE {table} SET Name = @name, ProfileJson = @json, Version = Version + 1, IsSample = @sample, IsArchived = @archived, ModifiedBy = @by, ModifiedAtUtc = @now WHERE CustomerKey = @key;";
        await using (var command = session.Command(transaction, sql))
        {
            DatabaseConnectionFactory.Parameter(command, "@key", profile.Id.Value);
            DatabaseConnectionFactory.Parameter(command, "@name", Truncate(profile.Name, 200));
            DatabaseConnectionFactory.Parameter(command, "@json", json);
            DatabaseConnectionFactory.Parameter(command, "@sample", session.Bool(isSample));
            DatabaseConnectionFactory.Parameter(command, "@archived", session.Bool(false));
            DatabaseConnectionFactory.Parameter(command, "@by", Truncate(auditContext.Actor, 256));
            DatabaseConnectionFactory.Parameter(command, "@now", session.Date(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ProjectCustomerAsync(session, transaction, profile, isSample, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StoredCustomerProfile(profile, (version ?? 0) + 1, isSample, auditContext.Actor, now);
    }

    public async Task ArchiveCustomerProfileAsync(string customerKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null,
            $"UPDATE {session.Table("secureplus", "CustomerProfile")} SET IsArchived = @archived, ModifiedBy = @by, ModifiedAtUtc = @now WHERE CustomerKey = @key;");
        DatabaseConnectionFactory.Parameter(command, "@archived", session.Bool(true));
        DatabaseConnectionFactory.Parameter(command, "@by", Truncate(auditContext.Actor, 256));
        DatabaseConnectionFactory.Parameter(command, "@now", session.Date(timeProvider.GetUtcNow()));
        DatabaseConnectionFactory.Parameter(command, "@key", customerKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes the profile into the EaaS v3 customer tables (called inside the save transaction).</summary>
    private async Task ProjectCustomerAsync(SqlSession session, DbTransaction transaction, CustomerProfile profile, bool isSample, CancellationToken cancellationToken)
    {
        var link = await EnsureCustomerAsync(session, transaction, profile.Id.Value, profile.Name, isSample, cancellationToken);
        var details = profile.Details;
        var now = timeProvider.GetUtcNow();

        // cust.Customer
        await using (var update = session.Command(transaction,
                         $"UPDATE {session.Table("cust", "Customer")} SET Name = @name, CrmAccountId = @crm, IndustryCode = @industry, OrgSizeBandCode = @size, Country = @country, " +
                         "StateRegion = @state, Notes = @notes, IsActive = @active, CustomerKey = @key, AddressLine1 = @a1, AddressLine2 = @a2, City = @city, PostalCode = @postal, " +
                         "Phone = @phone, Email = @email, Website = @web, ModifiedBy = @by, ModifiedAtUtc = @now WHERE CustomerId = @id;"))
        {
            Parameters(update, ("@name", Truncate(profile.Name, 200)), ("@crm", Text(details.CrmAccountId, 50)), ("@industry", Text(details.IndustryCode, 40)),
                ("@size", Text(details.OrganizationSizeCode, 20)), ("@country", Text(details.Country, 60)), ("@state", Text(details.StateRegion, 60)),
                ("@notes", Text(profile.Notes, 2000) ?? (isSample ? "Sample customer created by Secure Plus Agent setup." : null)), ("@active", session.Bool(true)),
                ("@key", profile.Id.Value), ("@a1", Text(details.AddressLine1, 200)), ("@a2", Text(details.AddressLine2, 200)), ("@city", Text(details.City, 100)),
                ("@postal", Text(details.PostalCode, 20)), ("@phone", Text(details.Phone, 50)), ("@email", Text(details.Email, 320)), ("@web", Text(details.Website, 300)),
                ("@by", Truncate(auditContext.Actor, 256)), ("@now", session.Date(now)), ("@id", link.CustomerId));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        // cust.Tenant (+ audit.TenantConnection)
        var tenantIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var existingTenants = await QueryRowsAsync(session, transaction,
            $"SELECT TenantId, TenantKey, TenantGuid FROM {session.Table("cust", "Tenant")} WHERE CustomerId = @id;", ("@id", link.CustomerId), cancellationToken);
        foreach (var tenant in profile.Tenants)
        {
            var match = existingTenants.FirstOrDefault(row => string.Equals(row[1] as string, tenant.TenantKey, StringComparison.OrdinalIgnoreCase))
                        ?? existingTenants.FirstOrDefault(row => tenant.TenantId is { } id && row[2] is not null && string.Equals(Convert.ToString(row[2], CultureInfo.InvariantCulture), id.ToString(), StringComparison.OrdinalIgnoreCase));
            var values = new (string, object?)[]
            {
                ("@customer", link.CustomerId), ("@guid", tenant.TenantId is { } guid ? session.Sqlite ? guid.ToString() : guid : null), ("@domain", Text(tenant.PrimaryDomain, 255)),
                ("@cloud", tenant.CloudEnvironment.ToString()), ("@notes", Text($"{tenant.DisplayName} ({tenant.TenantKey})", 1000)), ("@key", Text(tenant.TenantKey, 64)),
                ("@display", Text(tenant.DisplayName, 200)), ("@tname", Text(tenant.TenantName, 255)), ("@client", tenant.ApplicationRegistration?.ClientId.ToString()),
                ("@auth", tenant.ApplicationRegistration?.AuthenticationType.ToString()), ("@now", session.Date(now))
            };
            int tenantId;
            if (match is not null)
            {
                tenantId = Convert.ToInt32(match[0], CultureInfo.InvariantCulture);
                await using var update = session.Command(transaction,
                    $"UPDATE {session.Table("cust", "Tenant")} SET TenantGuid = @guid, PrimaryDomain = @domain, CloudEnvironmentCode = @cloud, Notes = @notes, TenantKey = @key, " +
                    "DisplayName = @display, TenantName = @tname, ClientId = @client, AuthenticationType = @auth, ModifiedAtUtc = @now WHERE TenantId = @tenant;");
                Parameters(update, [.. values, ("@tenant", tenantId)]);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var insert = session.Command(transaction,
                    $"INSERT INTO {session.Table("cust", "Tenant")} (CustomerId, TenantGuid, PrimaryDomain, CloudEnvironmentCode, Notes, TenantKey, DisplayName, TenantName, ClientId, " +
                    $"AuthenticationType, ModifiedAtUtc) VALUES (@customer, @guid, @domain, @cloud, @notes, @key, @display, @tname, @client, @auth, @now); {session.IdentitySql}");
                Parameters(insert, values);
                tenantId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            tenantIds[tenant.TenantKey] = tenantId;
            await UpsertTenantConnectionAsync(session, transaction, tenantId, tenant.ApplicationRegistration?.ClientId, cancellationToken);
        }

        foreach (var removed in existingTenants.Where(row => !tenantIds.ContainsValue(Convert.ToInt32(row[0], CultureInfo.InvariantCulture))))
        {
            var id = Convert.ToInt32(removed[0], CultureInfo.InvariantCulture);
            await ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("audit", "TenantConnection")} WHERE TenantId = @id;", ("@id", id), cancellationToken);
            await ExecuteAsync(session, transaction,
                $"DELETE FROM {session.Table("cust", "Tenant")} WHERE TenantId = @id AND NOT EXISTS (SELECT 1 FROM {session.Table("cust", "Engagement")} WHERE TenantId = @id) " +
                $"AND NOT EXISTS (SELECT 1 FROM {session.Table("cust", "TenantLicense")} WHERE TenantId = @id);", ("@id", id), cancellationToken);
        }

        // cust.Engagement: GDS people, service tier and primary tenant.
        if (link.EngagementId is { } engagementId)
        {
            int? tierId = null;
            if (!string.IsNullOrWhiteSpace(details.ServiceTierCode))
                tierId = await ScalarIntAsync(session, transaction, $"SELECT ServiceTierId FROM {session.Table("svc", "ServiceTier")} WHERE Code = @key;", details.ServiceTierCode, cancellationToken);
            int? primaryTenant = profile.PrimaryTenant is { } primary && tenantIds.TryGetValue(primary.TenantKey, out var primaryId) ? primaryId : null;
            await using var update = session.Command(transaction,
                $"UPDATE {session.Table("cust", "Engagement")} SET Name = @name, SalesOwner = @sales, TechnicalLead = @tech, ProjectManager = @pm, " +
                "CurrentServiceTierId = COALESCE(@tier, CurrentServiceTierId), TenantId = COALESCE(@tenant, TenantId) WHERE EngagementId = @engagement;");
            Parameters(update, ("@name", Truncate($"{profile.Name} - Secure Plus", 200)), ("@sales", Text(details.SalesLead, 200)), ("@tech", Text(details.TechnicalLead, 200)),
                ("@pm", Text(details.ProjectManager, 200)), ("@tier", tierId), ("@tenant", primaryTenant), ("@engagement", engagementId));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        // cust.Contact: matched by e-mail (or name); removed contacts are deleted unless other EaaS rows refer to them.
        var existingContacts = await QueryRowsAsync(session, transaction,
            $"SELECT ContactId, Name, Email FROM {session.Table("cust", "Contact")} WHERE CustomerId = @id;", ("@id", link.CustomerId), cancellationToken);
        var kept = new HashSet<int>();
        var order = 0;
        foreach (var contact in profile.Contacts)
        {
            var match = existingContacts.FirstOrDefault(row => !kept.Contains(Convert.ToInt32(row[0], CultureInfo.InvariantCulture)) &&
                                                               (!string.IsNullOrWhiteSpace(contact.Email) && string.Equals(row[2] as string, contact.Email.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                                                string.Equals(row[1] as string, contact.Name.Trim(), StringComparison.OrdinalIgnoreCase)));
            var role = contact.Role ?? (contact.IsChangeApprover ? Application.Reference.ContactRoles.ChangeApprover : contact.IsPrimary ? Application.Reference.ContactRoles.PrimaryContact : null);
            var values = new (string, object?)[]
            {
                ("@customer", link.CustomerId), ("@name", Truncate(contact.Name.Trim(), 200)), ("@email", Text(contact.Email, 320)), ("@title", Text(contact.Title, 200)),
                ("@role", Text(role, 40)), ("@owner", session.Bool(contact.IsDecisionOwner || contact.IsChangeApprover)), ("@phone", Text(contact.Phone, 50)),
                ("@mobile", Text(contact.MobilePhone, 50)), ("@primary", session.Bool(contact.IsPrimary)), ("@approver", session.Bool(contact.IsChangeApprover)), ("@order", ++order)
            };
            if (match is not null)
            {
                var contactId = Convert.ToInt32(match[0], CultureInfo.InvariantCulture);
                kept.Add(contactId);
                await using var update = session.Command(transaction,
                    $"UPDATE {session.Table("cust", "Contact")} SET Name = @name, Email = @email, Title = @title, RoleCode = @role, IsDecisionOwner = @owner, Phone = @phone, " +
                    "MobilePhone = @mobile, IsPrimary = @primary, IsChangeApprover = @approver, SortOrder = @order WHERE ContactId = @contact;");
                Parameters(update, [.. values, ("@contact", contactId)]);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var insert = session.Command(transaction,
                    $"INSERT INTO {session.Table("cust", "Contact")} (CustomerId, Name, Email, Title, RoleCode, IsDecisionOwner, Phone, MobilePhone, IsPrimary, IsChangeApprover, SortOrder) " +
                    $"VALUES (@customer, @name, @email, @title, @role, @owner, @phone, @mobile, @primary, @approver, @order); {session.IdentitySql}");
                Parameters(insert, values);
                kept.Add(Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture));
            }
        }

        foreach (var removed in existingContacts.Select(row => Convert.ToInt32(row[0], CultureInfo.InvariantCulture)).Where(id => !kept.Contains(id)))
            await ExecuteAsync(session, transaction,
                $"DELETE FROM {session.Table("cust", "Contact")} WHERE ContactId = @id " +
                $"AND NOT EXISTS (SELECT 1 FROM {session.Table("cust", "RequirementResult")} WHERE OwnerContactId = @id) " +
                $"AND NOT EXISTS (SELECT 1 FROM {session.Table("cust", "QuestionResponse")} WHERE DecidedByContactId = @id) " +
                $"AND NOT EXISTS (SELECT 1 FROM {session.Table("cust", "Exception")} WHERE ApprovedByContactId = @id);", ("@id", removed), cancellationToken);
    }

    private static async Task UpsertTenantConnectionAsync(SqlSession session, DbTransaction transaction, int tenantId, Guid? clientId, CancellationToken cancellationToken)
    {
        var table = session.Table("audit", "TenantConnection");
        if (clientId is null)
        {
            await ExecuteAsync(session, transaction, $"DELETE FROM {table} WHERE TenantId = @id AND AppRoleCode = 'Collector';", ("@id", tenantId), cancellationToken);
            return;
        }

        var existing = await ScalarIntAsync(session, transaction, $"SELECT TenantConnectionId FROM {table} WHERE TenantId = @key AND AppRoleCode = 'Collector';", tenantId, cancellationToken);
        var app = session.Sqlite ? clientId.Value.ToString() : (object)clientId.Value;
        if (existing is { } connectionId)
        {
            await using var update = session.Command(transaction, $"UPDATE {table} SET AppId = @app WHERE TenantConnectionId = @connection;");
            Parameters(update, ("@app", app), ("@connection", connectionId));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var insert = session.Command(transaction,
                $"INSERT INTO {table} (TenantId, AppRoleCode, AppId, ConsentStatusCode) VALUES (@tenant, 'Collector', @app, 'Unknown');");
            Parameters(insert, ("@tenant", tenantId), ("@app", app));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task RecordTenantLicensesAsync(string customerKey, string tenantKey, IReadOnlyList<TenantLicenseCount> licenses, DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        if (licenses.Count == 0) return;
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var tenant = await ScalarIntAsync(session, transaction,
            $"SELECT t.TenantId FROM {session.Table("cust", "Tenant")} AS t JOIN {session.Table("secureplus", "CustomerLink")} AS l ON l.CustomerId = t.CustomerId " +
            $"WHERE l.CustomerKey = @key AND t.TenantKey = '{tenantKey.Replace("'", "''", StringComparison.Ordinal)}';", customerKey, cancellationToken);
        if (tenant is null) return;
        var skus = session.Table("kb", "LicenseSku");
        var tenantLicenses = session.Table("cust", "TenantLicense");
        object date = session.Sqlite ? asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : asOf.ToDateTime(TimeOnly.MinValue);
        foreach (var license in licenses)
        {
            var skuId = await ScalarIntAsync(session, transaction, $"SELECT LicenseSkuId FROM {skus} WHERE SkuPartNumber = @key;", license.SkuPartNumber, cancellationToken);
            if (skuId is null)
            {
                await using var insert = session.Command(transaction,
                    $"INSERT INTO {skus} (SkuPartNumber, Name, Vendor, IsAddOn, Notes) VALUES (@sku, @sku, 'Microsoft', @addon, 'Seen in a customer tenant by Secure Plus.'); {session.IdentitySql}");
                Parameters(insert, ("@sku", Truncate(license.SkuPartNumber, 100)), ("@addon", session.Bool(false)));
                skuId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            await using (var delete = session.Command(transaction, $"DELETE FROM {tenantLicenses} WHERE TenantId = @tenant AND LicenseSkuId = @sku AND AsOfDate = @date;"))
            {
                Parameters(delete, ("@tenant", tenant), ("@sku", skuId), ("@date", date));
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var add = session.Command(transaction, $"INSERT INTO {tenantLicenses} (TenantId, LicenseSkuId, AsOfDate, Quantity, Assigned) VALUES (@tenant, @sku, @date, @quantity, @assigned);");
            Parameters(add, ("@tenant", tenant), ("@sku", skuId), ("@date", date), ("@quantity", license.Quantity), ("@assigned", license.Assigned));
            await add.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Records the outcome of a tenant permission check in audit.TenantConnection.</summary>
    public async Task RecordTenantVerificationAsync(string customerKey, string tenantKey, string consentStatusCode, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null,
            $"UPDATE {session.Table("audit", "TenantConnection")} SET ConsentStatusCode = @status, LastVerifiedAt = @now WHERE AppRoleCode = 'Collector' AND TenantId IN (" +
            $"SELECT t.TenantId FROM {session.Table("cust", "Tenant")} AS t JOIN {session.Table("secureplus", "CustomerLink")} AS l ON l.CustomerId = t.CustomerId " +
            "WHERE l.CustomerKey = @customer AND t.TenantKey = @tenant);");
        Parameters(command, ("@status", Truncate(consentStatusCode, 20)), ("@now", session.Date(timeProvider.GetUtcNow())), ("@customer", customerKey), ("@tenant", tenantKey));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Parameters(DbCommand command, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values) DatabaseConnectionFactory.Parameter(command, name, value);
    }

    private static async Task ExecuteAsync(SqlSession session, DbTransaction transaction, string sql, (string, object?) parameter, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        Parameters(command, parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int?> ScalarIntAsync(SqlSession session, DbTransaction transaction, string sql, object key, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        DatabaseConnectionFactory.Parameter(command, "@key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<List<object?[]>> QueryRowsAsync(SqlSession session, DbTransaction transaction, string sql, (string, object?) parameter, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        Parameters(command, parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++) row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }

    private static string? Text(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), length);
}
