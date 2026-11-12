namespace ArchiTech.Api.Domain;

public sealed class SecurePlusDecision
{
    public required string Id { get; set; }
    public required string CatalogVersion { get; set; }
    public required string BaselineReferences { get; set; }
    public required string Phase { get; set; }
    public required string Category { get; set; }
    public required string Topic { get; set; }
    public required string Question { get; set; }
    public required string Rationale { get; set; }
    public required string AnswerType { get; set; }
    public required string SuggestedAnswers { get; set; }
    public required string ConfigurationImpact { get; set; }
    public required string DecisionOwner { get; set; }
    public required string RequiredParticipants { get; set; }
    public required string RequiredBy { get; set; }
    public required string EvidenceRequired { get; set; }
    public required string DefaultIfUndecided { get; set; }
    public required string EngineeringNotes { get; set; }
}

public interface IDiscoveryDecisionResponse
{
    string CatalogVersion { get; set; }
    string DecisionId { get; set; }
    string Status { get; set; }
    string? Answer { get; set; }
    string? Notes { get; set; }
    string UpdatedBy { get; set; }
    DateTime UpdatedUtc { get; set; }
}

public sealed class EngagementDecisionResponse : IDiscoveryDecisionResponse
{
    public long Id { get; set; }
    public Guid EngagementId { get; set; }
    public required string CatalogVersion { get; set; }
    public required string DecisionId { get; set; }
    public required string Status { get; set; }
    public string? Answer { get; set; }
    public string? Notes { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ProfileDecisionResponse : IDiscoveryDecisionResponse
{
    public long Id { get; set; }
    public Guid DeploymentProfileId { get; set; }
    public required string CatalogVersion { get; set; }
    public required string DecisionId { get; set; }
    public required string Status { get; set; }
    public string? Answer { get; set; }
    public string? Notes { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class SecurePlusWorkItem
{
    public required string Id { get; set; }
    public required string CatalogVersion { get; set; }
    public required string PhaseReference { get; set; }
    public required string Phase { get; set; }
    public required string Workstream { get; set; }
    public int TaskNumber { get; set; }
    public required string TaskName { get; set; }
    public required string SubTaskNumber { get; set; }
    public required string SubTaskName { get; set; }
    public required string GroupAbbreviation { get; set; }
    public int GroupItemNumber { get; set; }
    public required string Control { get; set; }
    public required string ControlDescription { get; set; }
    public required string Deliverable { get; set; }
    public required string Dependency { get; set; }
    public required string AppliesWhen { get; set; }
    public required string LicenseRequirement { get; set; }
    public decimal EstimatedHoursMin { get; set; }
    public decimal EstimatedHoursMax { get; set; }
    public decimal DurationDaysMin { get; set; }
    public decimal DurationDaysMax { get; set; }
    public bool Parallelizable { get; set; }
    public bool SupportHandoff { get; set; }
    public required string ComplexityDriver { get; set; }
    public required string LeadershipNotes { get; set; }
}

public sealed class SecurePlusBaselineSetting
{
    public required string Id { get; set; }
    public required string CatalogVersion { get; set; }
    public required string BaselineReference { get; set; }
    public required string Phase { get; set; }
    public required string Workstream { get; set; }
    public int TaskNumber { get; set; }
    public required string TaskName { get; set; }
    public required string GroupAbbreviation { get; set; }
    public int GroupItemNumber { get; set; }
    public required string Control { get; set; }
    public required string SettingCategory { get; set; }
    public required string SettingName { get; set; }
    public required string TargetAction { get; set; }
    public required string ReviewPath { get; set; }
    public required string DeploymentMethod { get; set; }
    public required string ValidationMethod { get; set; }
    public required string EvidenceRequired { get; set; }
    public required string AppliesWhen { get; set; }
    public required string LicenseRequirement { get; set; }
    public required string ImplementationMode { get; set; }
    public required string PilotGroup { get; set; }
    public required string Exclusions { get; set; }
    public required string ExceptionCriteria { get; set; }
    public required string RollbackMethod { get; set; }
    public required string ReviewCadence { get; set; }
    public required string SupportOwner { get; set; }
    public required string EngineeringNotes { get; set; }
}

public sealed class TargetModelTable
{
    public required string Id { get; set; }
    public required string CatalogVersion { get; set; }
    public required string Schema { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string EstimatedRows { get; set; }
    public required string PrimarySources { get; set; }
    public required string TemporalHistory { get; set; }
    public ICollection<TargetModelColumn> Columns { get; set; } = [];
}

public sealed class TargetModelColumn
{
    public required string TableId { get; set; }
    public required string CatalogVersion { get; set; }
    public TargetModelTable Table { get; set; } = null!;
    public int ColumnOrdinal { get; set; }
    public required string Name { get; set; }
    public required string DataType { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public string? ForeignKeyTarget { get; set; }
    public bool IsUniqueKeyPart { get; set; }
    public bool IsIndexed { get; set; }
}

public sealed class StandardsReferenceSource
{
    public required string Id { get; set; }
    public required string CatalogVersion { get; set; }
    public required string Area { get; set; }
    public required string Name { get; set; }
    public required string FileName { get; set; }
    public required string Format { get; set; }
    public required string SourceVersion { get; set; }
    public required string Status { get; set; }
    public required string UsageBoundary { get; set; }
    public required string Description { get; set; }
    public int? ImportedRecordCount { get; set; }
    public string? RecordUnit { get; set; }
}
