namespace EngineeringWorkspace.Domain.GoldenTenant;

/// <summary>Area of a Golden Tenant expectation.</summary>
public enum ExpectationArea
{
    Control,
    Setting,
    ConditionalAccess,
    Intune,
    SecuritySetting,
    Policy,
    Assignment
}

/// <summary>Result of comparing a tenant against one Golden Tenant expectation (comparison engine: M5).</summary>
public enum ComparisonStatus
{
    Match,
    Mismatch,
    Missing,
    Additional,
    NotApplicable,
    Exception,
    UnableToEvaluate
}

/// <summary>
/// What the Golden Tenant baseline expects for one control, setting, policy or assignment. Policy content keeps
/// the structure of the GDS Conditional Access baseline and policy-set files; it is carried as raw JSON.
/// </summary>
public sealed record GoldenTenantExpectation(
    string ExpectationId,
    ExpectationArea Area,
    string DisplayName,
    string? ControlId,
    string? SettingPath,
    string? ExpectedValueJson,
    string? SourcePolicyArtifactId);

/// <summary>A versioned Golden Tenant baseline.</summary>
public sealed record GoldenTenantBaseline(string BaselineId, string Version, IReadOnlyList<GoldenTenantExpectation> Expectations);
