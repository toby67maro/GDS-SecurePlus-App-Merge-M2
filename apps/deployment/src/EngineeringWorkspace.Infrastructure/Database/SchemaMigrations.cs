using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EngineeringWorkspace.Application.Database;

namespace EngineeringWorkspace.Infrastructure.Database;

/// <summary>One embedded, versioned migration script.</summary>
public sealed record SchemaMigration(int Version, string Description, string Script, string Checksum)
{
    /// <summary>Splits the script into batches on lines that contain only GO.</summary>
    public IReadOnlyList<string> Batches => Regex.Split(Script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)
        .Select(batch => batch.Trim())
        .Where(batch => batch.Length > 0 && !batch.Split('\n').All(line => line.TrimStart().StartsWith("--", StringComparison.Ordinal) || line.Trim().Length == 0))
        .ToArray();
}

/// <summary>Expected table of the EaaS v3 baseline, from the generated manifest.</summary>
public sealed record ExpectedTable(string Schema, string Name, bool Temporal, IReadOnlyList<string> Columns);

/// <summary>
/// The embedded migration set. V0001 is generated from the EaaS v3 data dictionary; V0002 adds the Secure Plus
/// extension tables; V0003 adds customer, workspace and assessment history; V0004 assessment results and schedules; V0005 the control catalog table; V0006 the Secure Plus 2.0 extension tables; V0007 the shared data tables of 2.1; V0008 the roles, audit ledger and scheduling PC of 3.2. Version history is recorded in secureplus.SchemaVersion (a Secure Plus extension).
/// </summary>
public static class SchemaMigrations
{
    private static readonly Lazy<IReadOnlyList<ExpectedTable>> Manifest = new(LoadManifest);

    public static IReadOnlyList<SchemaMigration> For(DatabaseProvider provider)
    {
        var folder = provider == DatabaseProvider.SQLite ? "Sqlite" : "SqlServer";
        return
        [
            Load(folder, 1, "V0001__eaas_v3_baseline.sql", "EaaS v3 baseline (generated from GDS_ComplianceIQ_Data_Dictionary_v3)"),
            Load(folder, 2, "V0002__secureplus_extensions.sql", "Secure Plus extension tables"),
            Load(folder, 3, "V0003__secureplus_engagements.sql", "Secure Plus customer, workspace and assessment history"),
            Load(folder, 4, "V0004__secureplus_assessment_results.sql", "Secure Plus assessment results and schedules"),
            Load(folder, 5, "V0005__secureplus_catalog.sql", "Secure Plus control catalog table"),
            Load(folder, 6, "V0006__secureplus_v2.sql",
                "Secure Plus 2.0: seed versions, control details, schedules v2, specifications, baselines, change requests, exceptions and audit"),
            Load(folder, 7, "V0007__secureplus_shared_data.sql",
                "Secure Plus 2.1: settings, themes, workspaces, customer profiles and tenant secrets in the database; customer address and contact phone"),
            Load(folder, 8, "V0008__secureplus_roles_ledger.sql",
                "Secure Plus 3.2: per-user roles, append-only audit ledger, scheduling PC and database roles"),
            Load(folder, 9, "V0009__secureplus_40.sql", "Secure Plus 4.0: Industry list, change request templates, project task keys, details and history"),
            Load(folder, 10, "V0010__catalog_drop_licensing.sql", "Secure Plus 4.0: free-text licensing removed from the control catalogue"),
            Load(folder, 11, "V0011__tenant_inventory.sql", "Secure Plus 4.0 beta.2: tenant inventory (users, groups, members, devices) and scan type"),
            Load(folder, 12, "V0012__catalog_edits.sql", "Secure Plus 4.0 beta.2: Lead edits of the control catalogue and append-only edit history")
        ];
    }

    public static int RequiredVersion => 12;

    /// <summary>EaaS v3 tables plus the Secure Plus extension tables expected after all migrations.</summary>
    public static IReadOnlyList<ExpectedTable> ExpectedTables => Manifest.Value;

    /// <summary>Schemas created by the migrations (used to detect foreign or partially initialized databases).</summary>
    public static IReadOnlyList<string> Schemas => ExpectedTables.Select(table => table.Schema).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

    private static SchemaMigration Load(string folder, int version, string fileName, string description)
    {
        var name = $"EngineeringWorkspace.Schema.{folder}.{fileName}";
        using var stream = typeof(SchemaMigrations).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded migration {name} is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var script = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant();
        return new SchemaMigration(version, description, script, checksum);
    }

    private static IReadOnlyList<ExpectedTable> LoadManifest()
    {
        using var stream = typeof(SchemaMigrations).Assembly.GetManifestResourceStream("EngineeringWorkspace.Schema.eaas-v3-schema-manifest.json")
            ?? throw new InvalidOperationException("The embedded EaaS v3 schema manifest is missing from the build.");
        using var document = JsonDocument.Parse(stream);
        var tables = document.RootElement.GetProperty("tables").EnumerateArray()
            .Select(table => new ExpectedTable(table.GetProperty("schema").GetString()!, table.GetProperty("name").GetString()!, table.GetProperty("temporal").GetBoolean(),
                table.GetProperty("columns").EnumerateArray().Select(column => column.GetString()!).ToArray()))
            .ToList();
        tables.Add(new ExpectedTable("secureplus", "Workspace", false, ["WorkspaceId", "Name", "Directory", "CustomerId", "CreatedAtUtc", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "CredentialMetadata", false,
            ["CredentialId", "DisplayName", "CredentialType", "Provider", "CreatedAtUtc", "UpdatedAtUtc", "ExpirationDateUtc", "SecretReference", "CustomerId", "TenantId"]));
        tables.Add(new ExpectedTable("secureplus", "AuditEvent", false, ["AuditEventId", "EventType", "Summary", "OccurredAtUtc", "CustomerId", "TenantId"]));
        tables.Add(new ExpectedTable("secureplus", "CustomerLink", false, ["CustomerKey", "CustomerId", "EngagementId", "DisplayName", "IsSample", "CreatedAtUtc", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "AssessmentStatus", false,
            ["AssessmentRunId", "CustomerKey", "WorkspaceId", "Name", "StatusCode", "Score", "ControlsTotal", "ControlsPassed", "RequestedAtUtc", "CompletedAtUtc", "IsSample"]));
        tables.Add(new ExpectedTable("secureplus", "AssessmentFinding", false, ["AssessmentFindingId", "AssessmentRunId", "Area", "Control", "Result", "Severity", "Recommendation"]));
        tables.Add(new ExpectedTable("secureplus", "AssessmentResult", false, ["AssessmentResultId", "AssessmentRunId", "ControlId", "Area", "Control", "Result", "Severity", "Evidence", "Recommendation"]));
        tables.Add(new ExpectedTable("secureplus", "AssessmentSchedule", false,
            ["ScheduleId", "Name", "CustomerKeys", "Frequency", "DayOfWeek", "DayOfMonth", "TimeOfDay", "IsEnabled", "LastRunUtc", "NextRunUtc", "CreatedBy", "CreatedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "CatalogControl", false,
            ["ControlId", "ArtifactId", "CatalogVersion", "Number", "Kind", "KindName", "Category", "Platform", "Name", "Setting", "Value", "Description", "Source",
             "Frameworks", "Severity", "UserImpact", "Experience", "PainPoints", "Evaluation", "IsAutomatic", "PolicySets", "Path", "GraphResource",
             "OdataType", "SyncedAtUtc"]));
        // V0006 (Secure Plus 2.0).
        void Extend(string name, string[] columns)
        {
            var (schema, bare) = name.Contains('.') ? (name.Split('.')[0], name.Split('.')[1]) : ("secureplus", name);
            var index = tables.FindIndex(table => table.Schema == schema && table.Name == bare);
            tables[index] = tables[index] with { Columns = [.. tables[index].Columns, .. columns] };
        }

        Extend("AssessmentStatus", ["Source", "ScheduleId", "StartedAtUtc", "ResultsSaved", "ErrorMessage"]);
        Extend("AssessmentResult", ["RecordedAtUtc"]);
        Extend("AssessmentSchedule", ["CustomerKey", "DaysOfWeek", "Months", "IntervalHours", "IntervalWeeks", "WindowStart", "WindowEnd", "MonthlyMode", "WeekOfMonth", "TimeZoneId", "AnchorDate", "LastStatus", "LastMessage", "ModifiedAtUtc"]);
        tables.Add(new ExpectedTable("secureplus", "SeedVersion", false, ["Dataset", "Version", "ContentHash", "RowsApplied", "AppliedAtUtc", "AppliedBy", "Summary"]));
        tables.Add(new ExpectedTable("secureplus", "ReferenceValue", false, ["Category", "Code", "Name", "Description", "SortOrder"]));
        tables.Add(new ExpectedTable("secureplus", "ControlDetail", false, ["ControlId", "SeedVersion", "SolutionArea", "ExperienceArea", "VariantOf", "VariantLabel", "ComplianceStandards", "EstimatedMinutes", "Difficulty", "Effort", "RiskLevel", "Benefits", "ExperienceImpact", "GraphRequest", "GraphCommand", "PowerShell", "JsonBody", "SettingPath", "GpoPath", "Registry", "Generated", "UpdatedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "ControlAlias", false, ["Alias", "ControlId", "Reason"]));
        tables.Add(new ExpectedTable("secureplus", "ScheduleRun", false, ["ScheduleRunId", "ScheduleId", "StartedAtUtc", "CompletedAtUtc", "StatusCode", "Message", "Host", "AssessmentRunIds"]));
        tables.Add(new ExpectedTable("secureplus", "Specification", false, ["SpecificationKey", "Name", "Description", "IsStandard", "IsSystem", "IsModified", "BasedOnKey", "StatusCode", "SeedVersion", "CreatedBy", "CreatedAtUtc", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "SpecificationItem", false, ["SpecificationKey", "ItemType", "ItemKey"]));
        tables.Add(new ExpectedTable("secureplus", "SpecificationAssignment", false, ["CustomerKey", "SpecificationKey", "AssignedBy", "AssignedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "Baseline", false, ["BaselineKey", "Name", "Description", "IsActive", "IsSystem", "IsModified", "SeedVersion", "CreatedBy", "CreatedAtUtc", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "BaselineItem", false, ["BaselineKey", "ItemType", "ItemKey"]));
        tables.Add(new ExpectedTable("secureplus", "ChangeRequest", false, ["ChangeRequestId", "CustomerKey", "Title", "Description", "Risk", "Impact", "RollbackPlan", "RequestedBy", "ApprovedBy", "Owner", "WindowStartUtc", "WindowEndUtc", "StatusCode", "DecisionNote", "CreatedAtUtc", "ModifiedAtUtc", "DecidedAtUtc", "CompletedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "ChangeRequestItem", false, ["ChangeRequestId", "ItemType", "ItemKey"]));
        tables.Add(new ExpectedTable("secureplus", "ChangeExecution", false, ["ChangeExecutionId", "ChangeRequestId", "ModeCode", "StatusCode", "StartedAtUtc", "CompletedAtUtc", "ExecutedBy", "Log", "ReassessmentRunId"]));
        tables.Add(new ExpectedTable("secureplus", "CustomerException", false, ["ExceptionId", "CustomerKey", "ControlId", "Reason", "ApprovedBy", "ExpiresAtUtc", "StatusCode", "CreatedBy", "CreatedAtUtc", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "EntityAudit", false, ["EntityAuditId", "EntityType", "EntityKey", "Property", "OldValue", "NewValue", "ChangedBy", "ChangedAtUtc"]));

        // V0007 (Secure Plus 2.1).
        Extend("cust.Customer", ["CustomerKey", "AddressLine1", "AddressLine2", "City", "PostalCode", "Phone", "Email", "Website", "ModifiedBy", "ModifiedAtUtc"]);
        Extend("cust.Contact", ["Phone", "MobilePhone", "IsPrimary", "IsChangeApprover", "SortOrder"]);
        Extend("cust.Tenant", ["TenantKey", "DisplayName", "TenantName", "ClientId", "AuthenticationType", "ModifiedAtUtc"]);
        Extend("cust.Exception", ["SecurePlusExceptionId"]);
        Extend("deploy.ChangeRequest", ["SecurePlusChangeRequestId"]);
        Extend("Workspace", ["Description", "CustomerKey", "ThemeKey", "Environment", "IsArchived", "CreatedBy", "ModifiedBy", "SettingsJson"]);
        tables.Add(new ExpectedTable("secureplus", "Setting", false, ["Scope", "ComputerName", "SettingKey", "Value", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "DatabaseProperty", false, ["PropertyKey", "Value", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "Theme", false, ["ThemeKey", "Name", "Description", "PropertiesJson", "IsSystem", "CreatedBy", "CreatedAtUtc", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "CustomerProfile", false, ["CustomerKey", "Name", "ProfileJson", "Version", "IsSample", "IsArchived", "CreatedBy", "CreatedAtUtc", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "TenantSecret", false, ["SecretReference", "CipherText", "KeyId", "CustomerKey", "TenantKey", "ExpiresAtUtc", "CreatedBy", "CreatedAtUtc", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "ClientInstall", false, ["ComputerName", "UserName", "AppVersion", "FirstSeenUtc", "LastSeenUtc", "HasOrganizationKey"]));

        // V0008 (Secure Plus 3.2).
        tables.Add(new ExpectedTable("secureplus", "AppUser", false, ["WindowsAccount", "Upn", "DisplayName", "RoleCode", "IsActive", "LastSeenUtc", "CreatedBy", "CreatedAtUtc", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "SchedulerHost", false, ["HostKey", "ComputerName", "DesignatedBy", "DesignatedAtUtc", "HeartbeatUtc", "LastRunUtc", "LastStatus", "LastMessage", "AppVersion"]));
        tables.Add(new ExpectedTable("secureplus", "AuditLedger", false, ["EventId", "OccurredAtUtc", "Category", "EventType", "Outcome", "Actor", "ComputerName", "WorkspaceId", "CustomerId", "TenantId", "Summary", "PropertiesJson"]));

        // V0009 (Secure Plus 4.0).
        Extend("dlv.ProjectTask", ["TaskKey", "SortOrder", "Notes", "ActualFinishDate", "ChangeRequestIds", "ControlIds", "ScheduleRank", "PinnedStartDate"]);
        tables.Add(new ExpectedTable("secureplus", "Industry", false, ["Code", "Name", "SortOrder", "IsActive", "IsSystem", "IsModified", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "ChangeRequestTemplate", false,
            ["TemplateKey", "CustomerKey", "Name", "Version", "SourceTemplateKey", "SourceVersion", "IsSystem", "ContentJson", "CreatedBy", "CreatedAtUtc", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "ProjectTaskEvent", false,
            ["ProjectTaskEventId", "ProjectPlanId", "TaskKey", "EventType", "Summary", "DetailJson", "AttachmentName", "Attachment", "CreatedBy", "CreatedAtUtc"]));

        // V0011 (Secure Plus 4.0 beta.2).
        Extend("AssessmentStatus", ["ScanType"]);
        Extend("AssessmentSchedule", ["ScanType"]);
        tables.Add(new ExpectedTable("secureplus", "TenantUser", false, ["TenantId", "ObjectId", "CustomerKey", "DisplayName", "UserPrincipalName", "AccountEnabled", "UserType",
            "Department", "JobTitle", "Licenses", "AdminRoles", "FirstSeenUtc", "LastSeenUtc", "RemovedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "TenantGroup", false, ["TenantId", "ObjectId", "CustomerKey", "DisplayName", "Kind", "IsDynamic", "MemberCount", "FirstSeenUtc",
            "LastSeenUtc", "RemovedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "TenantGroupMember", false, ["TenantId", "GroupObjectId", "MemberObjectId", "CustomerKey", "MemberType", "CollectedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "TenantDevice", false, ["TenantId", "DeviceId", "CustomerKey", "DeviceName", "OperatingSystem", "ComplianceState", "Ownership",
            "LastSyncUtc", "FirstSeenUtc", "LastSeenUtc", "RemovedAtUtc"]));

        // V0012 (Secure Plus 4.0 beta.2).
        tables.Add(new ExpectedTable("secureplus", "CatalogControlEdit", false, ["ControlId", "BasedOnControlId", "IsHidden", "ContentJson", "BaseHash", "Version", "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "CatalogPolicySetEdit", false, ["SetId", "BasedOnSetId", "IsHidden", "DisplayName", "DeploymentOrder", "ControlIdsJson", "Version",
            "ModifiedBy", "ModifiedAtUtc"]));
        tables.Add(new ExpectedTable("secureplus", "CatalogEditHistory", false, ["CatalogEditHistoryId", "ItemType", "ItemId", "Action", "BeforeJson", "AfterJson", "ChangedBy", "ChangedAtUtc"]));
        return tables;
    }
}
