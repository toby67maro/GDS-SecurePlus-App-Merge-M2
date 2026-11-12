namespace EngineeringWorkspace.Application.Common;

/// <summary>State of one readiness check.</summary>
public enum CheckState
{
    NotRun,
    Passed,
    Failed,
    Warning,
    NotApplicable
}
