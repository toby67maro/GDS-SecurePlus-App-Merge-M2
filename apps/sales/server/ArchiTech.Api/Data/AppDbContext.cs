using ArchiTech.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Engagement> Engagements => Set<Engagement>();
    public DbSet<EngagementAnswer> EngagementAnswers => Set<EngagementAnswer>();
    public DbSet<WbsTask> WbsTasks => Set<WbsTask>();
    public DbSet<EngagementTask> EngagementTasks => Set<EngagementTask>();
    public DbSet<DeploymentProfile> DeploymentProfiles => Set<DeploymentProfile>();
    public DbSet<DeploymentProfileSetupStep> DeploymentProfileSetupSteps => Set<DeploymentProfileSetupStep>();
    public DbSet<DeploymentFinding> DeploymentFindings => Set<DeploymentFinding>();
    public DbSet<AssessmentRun> AssessmentRuns => Set<AssessmentRun>();
    public DbSet<AssessmentResult> AssessmentResults => Set<AssessmentResult>();
    public DbSet<AssessmentScoreBreakdown> AssessmentScoreBreakdowns => Set<AssessmentScoreBreakdown>();
    public DbSet<AssessmentRecommendation> AssessmentRecommendations => Set<AssessmentRecommendation>();
    public DbSet<DeploymentChangePlan> DeploymentChangePlans => Set<DeploymentChangePlan>();
    public DbSet<DeploymentChangePlanItem> DeploymentChangePlanItems => Set<DeploymentChangePlanItem>();
    public DbSet<DeploymentChangePlanEvent> DeploymentChangePlanEvents => Set<DeploymentChangePlanEvent>();
    public DbSet<SecurePlusDecision> SecurePlusDecisions => Set<SecurePlusDecision>();
    public DbSet<EngagementDecisionResponse> EngagementDecisionResponses => Set<EngagementDecisionResponse>();
    public DbSet<ProfileDecisionResponse> ProfileDecisionResponses => Set<ProfileDecisionResponse>();
    public DbSet<SecurePlusWorkItem> SecurePlusWorkItems => Set<SecurePlusWorkItem>();
    public DbSet<SecurePlusBaselineSetting> SecurePlusBaselineSettings => Set<SecurePlusBaselineSetting>();
    public DbSet<TargetModelTable> TargetModelTables => Set<TargetModelTable>();
    public DbSet<TargetModelColumn> TargetModelColumns => Set<TargetModelColumn>();
    public DbSet<StandardsReferenceSource> StandardsReferenceSources => Set<StandardsReferenceSource>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.ToTable("Customers");
            entity.HasKey(customer => customer.Id);
            entity.Property(customer => customer.Name).HasMaxLength(200).IsRequired();
            entity.Property(customer => customer.NormalizedName).HasMaxLength(200).IsRequired();
            entity.Property(customer => customer.ContactName).HasMaxLength(160);
            entity.Property(customer => customer.ContactEmail).HasMaxLength(254);
            entity.HasIndex(customer => customer.NormalizedName).IsUnique();
            entity.HasMany(customer => customer.Engagements)
                .WithOne(engagement => engagement.Customer)
                .HasForeignKey(engagement => engagement.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(customer => customer.AssessmentRuns)
                .WithOne(run => run.Customer)
                .HasForeignKey(run => run.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Engagement>(entity =>
        {
            entity.ToTable("Engagements");
            entity.HasKey(engagement => engagement.Id);
            entity.Property(engagement => engagement.Salesperson).HasMaxLength(160).IsRequired();
            entity.Property(engagement => engagement.OwnerId).HasMaxLength(200);
            entity.Property(engagement => engagement.OwnerEmail).HasMaxLength(320);
            entity.Property(engagement => engagement.DeploymentSyncStatus).HasMaxLength(24).IsRequired().HasDefaultValue("Pending");
            entity.Property(engagement => engagement.DeploymentSyncMessage).HasMaxLength(300);
            entity.HasIndex(engagement => engagement.OwnerId);
            entity.Property(engagement => engagement.Notes).HasMaxLength(4000);
            entity.Property(engagement => engagement.PlannerHandoffStatus).HasMaxLength(24).IsRequired();
            entity.Property(engagement => engagement.PlannerPlanId).HasMaxLength(200);
            entity.Property(engagement => engagement.PlannerHandoffMessage).HasMaxLength(300);
            entity.Property(engagement => engagement.OneTimeHoursMin).HasPrecision(9, 2);
            entity.Property(engagement => engagement.OneTimeHoursMax).HasPrecision(9, 2);
            entity.Property(engagement => engagement.MonthlyHoursMin).HasPrecision(9, 2);
            entity.Property(engagement => engagement.MonthlyHoursMax).HasPrecision(9, 2);
            entity.HasIndex(engagement => engagement.CreatedUtc);
            entity.HasMany(engagement => engagement.Answers)
                .WithOne(answer => answer.Engagement)
                .HasForeignKey(answer => answer.EngagementId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(engagement => engagement.Tasks)
                .WithOne(task => task.Engagement)
                .HasForeignKey(task => task.EngagementId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EngagementAnswer>(entity =>
        {
            entity.ToTable("EngagementAnswers");
            entity.HasKey(answer => answer.Id);
            entity.Property(answer => answer.FieldKey).HasMaxLength(48).IsRequired();
            entity.Property(answer => answer.Value).HasMaxLength(240).IsRequired();
            entity.HasIndex(answer => new { answer.EngagementId, answer.FieldKey, answer.Value })
                .IsUnique();
        });

        modelBuilder.Entity<WbsTask>(entity =>
        {
            entity.ToTable("WbsTasks");
            entity.HasKey(task => task.Id);
            entity.Property(task => task.Id).HasMaxLength(20);
            entity.Property(task => task.Phase).HasMaxLength(100).IsRequired();
            entity.Property(task => task.Workstream).HasMaxLength(100).IsRequired();
            entity.Property(task => task.Name).HasMaxLength(200).IsRequired();
            entity.Property(task => task.Deliverable).HasMaxLength(1000).IsRequired();
            entity.Property(task => task.RuleKey).HasMaxLength(48).IsRequired();
            entity.Property(task => task.DurationMin).HasMaxLength(40).IsRequired();
            entity.Property(task => task.DurationMax).HasMaxLength(40).IsRequired();
            entity.Property(task => task.MinHours).HasPrecision(9, 2);
            entity.Property(task => task.MaxHours).HasPrecision(9, 2);
        });

        modelBuilder.Entity<EngagementTask>(entity =>
        {
            entity.ToTable("EngagementTasks");
            entity.HasKey(task => task.Id);
            entity.Property(task => task.WbsTaskId).HasMaxLength(20).IsRequired();
            entity.Property(task => task.MinHours).HasPrecision(9, 2);
            entity.Property(task => task.MaxHours).HasPrecision(9, 2);
            entity.HasIndex(task => new { task.EngagementId, task.WbsTaskId }).IsUnique();
            entity.HasOne(task => task.WbsTask)
                .WithMany(wbsTask => wbsTask.EngagementTasks)
                .HasForeignKey(task => task.WbsTaskId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeploymentProfile>(entity =>
        {
            entity.ToTable("DeploymentProfiles");
            entity.HasKey(profile => profile.Id);
            entity.Property(profile => profile.TenantId).HasMaxLength(36);
            entity.Property(profile => profile.PrimaryDomain).HasMaxLength(253);
            entity.Property(profile => profile.EntraApplicationClientId).HasMaxLength(36);
            entity.HasIndex(profile => profile.CustomerId).IsUnique();
            entity.HasOne(profile => profile.Customer)
                .WithOne(customer => customer.DeploymentProfile)
                .HasForeignKey<DeploymentProfile>(profile => profile.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeploymentProfileSetupStep>(entity =>
        {
            entity.ToTable("DeploymentProfileSetupSteps");
            entity.HasKey(step => step.Id);
            entity.Property(step => step.StepKey).HasMaxLength(48).IsRequired();
            entity.HasIndex(step => new { step.DeploymentProfileId, step.StepKey }).IsUnique();
            entity.HasOne(step => step.DeploymentProfile)
                .WithMany(profile => profile.SetupSteps)
                .HasForeignKey(step => step.DeploymentProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeploymentFinding>(entity =>
        {
            entity.ToTable("DeploymentFindings");
            entity.HasKey(finding => finding.Id);
            entity.Property(finding => finding.ControlId).HasMaxLength(32).IsRequired();
            entity.Property(finding => finding.Status).HasMaxLength(32).IsRequired();
            entity.Property(finding => finding.Notes).HasMaxLength(2000);
            entity.Property(finding => finding.Evidence).HasMaxLength(4000);
            entity.Property(finding => finding.ReviewedBy).HasMaxLength(160);
            entity.HasIndex(finding => new { finding.DeploymentProfileId, finding.ControlId }).IsUnique();
            entity.HasOne(finding => finding.DeploymentProfile)
                .WithMany(profile => profile.Findings)
                .HasForeignKey(finding => finding.DeploymentProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssessmentRun>(entity =>
        {
            entity.ToTable("AssessmentRuns");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Name).HasMaxLength(160).IsRequired();
            entity.Property(run => run.Assessor).HasMaxLength(160).IsRequired();
            entity.Property(run => run.Source).HasMaxLength(32).IsRequired();
            entity.Property(run => run.CatalogVersion).HasMaxLength(24).IsRequired();
            entity.Property(run => run.ReadinessRating).HasMaxLength(24).IsRequired();
            entity.HasIndex(run => new { run.CustomerId, run.StartedUtc });
            entity.HasOne(run => run.Engagement)
                .WithMany()
                .HasForeignKey(run => run.EngagementId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(run => run.Results)
                .WithOne(result => result.AssessmentRun)
                .HasForeignKey(result => result.AssessmentRunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(run => run.Breakdowns)
                .WithOne(breakdown => breakdown.AssessmentRun)
                .HasForeignKey(breakdown => breakdown.AssessmentRunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(run => run.Recommendations)
                .WithOne(recommendation => recommendation.AssessmentRun)
                .HasForeignKey(recommendation => recommendation.AssessmentRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssessmentResult>(entity =>
        {
            entity.ToTable("AssessmentResults");
            entity.HasKey(result => result.Id);
            entity.Property(result => result.ControlId).HasMaxLength(32).IsRequired();
            entity.Property(result => result.Status).HasMaxLength(32).IsRequired();
            entity.Property(result => result.Notes).HasMaxLength(2000);
            entity.Property(result => result.Evidence).HasMaxLength(4000);
            entity.Property(result => result.Assessor).HasMaxLength(160).IsRequired();
            entity.Property(result => result.ExceptionReason).HasMaxLength(1000);
            entity.HasIndex(result => new { result.AssessmentRunId, result.ControlId }).IsUnique();
        });

        modelBuilder.Entity<AssessmentScoreBreakdown>(entity =>
        {
            entity.ToTable("AssessmentScoreBreakdowns");
            entity.HasKey(breakdown => breakdown.Id);
            entity.Property(breakdown => breakdown.BreakdownType).HasMaxLength(24).IsRequired();
            entity.Property(breakdown => breakdown.Label).HasMaxLength(100).IsRequired();
            entity.HasIndex(breakdown => new
            {
                breakdown.AssessmentRunId,
                breakdown.BreakdownType,
                breakdown.Label
            }).IsUnique();
        });

        modelBuilder.Entity<AssessmentRecommendation>(entity =>
        {
            entity.ToTable("AssessmentRecommendations");
            entity.HasKey(recommendation => recommendation.Id);
            entity.Property(recommendation => recommendation.Severity).HasMaxLength(24).IsRequired();
            entity.Property(recommendation => recommendation.Text).HasMaxLength(1000).IsRequired();
            entity.HasIndex(recommendation => new
            {
                recommendation.AssessmentRunId,
                recommendation.Sequence
            }).IsUnique();
        });

        modelBuilder.Entity<DeploymentChangePlan>(entity =>
        {
            entity.ToTable("DeploymentChangePlans");
            entity.HasKey(plan => plan.Id);
            entity.Property(plan => plan.Title).HasMaxLength(160).IsRequired();
            entity.Property(plan => plan.Notes).HasMaxLength(2000);
            entity.Property(plan => plan.Status).HasMaxLength(24).IsRequired().IsConcurrencyToken();
            entity.Property(plan => plan.RequestedById).HasMaxLength(200).IsRequired();
            entity.Property(plan => plan.RequestedBy).HasMaxLength(160).IsRequired();
            entity.Property(plan => plan.ReviewedById).HasMaxLength(200);
            entity.Property(plan => plan.ReviewedBy).HasMaxLength(160);
            entity.Property(plan => plan.ReviewDecision).HasMaxLength(24);
            entity.Property(plan => plan.ReviewNotes).HasMaxLength(2000);
            entity.HasIndex(plan => new { plan.CustomerId, plan.CreatedUtc });
            entity.HasIndex(plan => plan.Status);
            entity.HasOne(plan => plan.Customer)
                .WithMany(customer => customer.DeploymentChangePlans)
                .HasForeignKey(plan => plan.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(plan => plan.AssessmentRun)
                .WithMany()
                .HasForeignKey(plan => plan.AssessmentRunId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(plan => plan.Items)
                .WithOne(item => item.DeploymentChangePlan)
                .HasForeignKey(item => item.DeploymentChangePlanId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(plan => plan.Events)
                .WithOne(item => item.DeploymentChangePlan)
                .HasForeignKey(item => item.DeploymentChangePlanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeploymentChangePlanItem>(entity =>
        {
            entity.ToTable("DeploymentChangePlanItems");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ControlId).HasMaxLength(32).IsRequired();
            entity.Property(item => item.ControlName).HasMaxLength(300).IsRequired();
            entity.Property(item => item.AssessmentStatus).HasMaxLength(32).IsRequired();
            entity.HasIndex(item => new { item.DeploymentChangePlanId, item.ControlId }).IsUnique();
            entity.HasIndex(item => new { item.DeploymentChangePlanId, item.Sequence }).IsUnique();
        });

        modelBuilder.Entity<DeploymentChangePlanEvent>(entity =>
        {
            entity.ToTable("DeploymentChangePlanEvents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EventType).HasMaxLength(32).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Actor).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Notes).HasMaxLength(2000);
            entity.HasIndex(item => new { item.DeploymentChangePlanId, item.OccurredUtc });
        });

        modelBuilder.Entity<SecurePlusDecision>(entity =>
        {
            entity.ToTable("SecurePlusDecisions");
            entity.HasKey(item => new { item.CatalogVersion, item.Id });
            entity.Property(item => item.Id).HasMaxLength(32);
            entity.Property(item => item.CatalogVersion).HasMaxLength(32).IsRequired();
            entity.Property(item => item.BaselineReferences).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Phase).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Category).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Topic).HasMaxLength(240).IsRequired();
            entity.Property(item => item.Question).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.Rationale).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.AnswerType).HasMaxLength(160).IsRequired();
            entity.Property(item => item.SuggestedAnswers).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.ConfigurationImpact).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.DecisionOwner).HasMaxLength(240).IsRequired();
            entity.Property(item => item.RequiredParticipants).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.RequiredBy).HasMaxLength(240).IsRequired();
            entity.Property(item => item.EvidenceRequired).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.DefaultIfUndecided).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.EngineeringNotes).HasMaxLength(4000).IsRequired();
            entity.HasIndex(item => new { item.CatalogVersion, item.Phase, item.Category });
        });

        modelBuilder.Entity<EngagementDecisionResponse>(entity =>
        {
            entity.ToTable("EngagementDecisionResponses");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.CatalogVersion).HasMaxLength(32).IsRequired();
            entity.Property(item => item.DecisionId).HasMaxLength(32).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(24).IsRequired();
            entity.Property(item => item.Answer).HasMaxLength(4000);
            entity.Property(item => item.Notes).HasMaxLength(2000);
            entity.Property(item => item.UpdatedBy).HasMaxLength(160).IsRequired();
            entity.HasIndex(item => new { item.EngagementId, item.CatalogVersion, item.DecisionId }).IsUnique();
            entity.HasOne<Engagement>()
                .WithMany()
                .HasForeignKey(item => item.EngagementId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SecurePlusDecision>()
                .WithMany()
                .HasForeignKey(item => new { item.CatalogVersion, item.DecisionId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProfileDecisionResponse>(entity =>
        {
            entity.ToTable("ProfileDecisionResponses");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.CatalogVersion).HasMaxLength(32).IsRequired();
            entity.Property(item => item.DecisionId).HasMaxLength(32).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(24).IsRequired();
            entity.Property(item => item.Answer).HasMaxLength(4000);
            entity.Property(item => item.Notes).HasMaxLength(2000);
            entity.Property(item => item.UpdatedBy).HasMaxLength(160).IsRequired();
            entity.HasIndex(item => new { item.DeploymentProfileId, item.CatalogVersion, item.DecisionId }).IsUnique();
            entity.HasOne<DeploymentProfile>().WithMany()
                .HasForeignKey(item => item.DeploymentProfileId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SecurePlusDecision>().WithMany()
                .HasForeignKey(item => new { item.CatalogVersion, item.DecisionId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SecurePlusWorkItem>(entity =>
        {
            entity.ToTable("SecurePlusWorkItems");
            entity.HasKey(item => new { item.CatalogVersion, item.Id });
            entity.Property(item => item.Id).HasMaxLength(48);
            entity.Property(item => item.CatalogVersion).HasMaxLength(32).IsRequired();
            entity.Property(item => item.PhaseReference).HasMaxLength(48).IsRequired();
            entity.Property(item => item.Phase).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Workstream).HasMaxLength(160).IsRequired();
            entity.Property(item => item.TaskName).HasMaxLength(240).IsRequired();
            entity.Property(item => item.SubTaskNumber).HasMaxLength(32).IsRequired();
            entity.Property(item => item.SubTaskName).HasMaxLength(240).IsRequired();
            entity.Property(item => item.GroupAbbreviation).HasMaxLength(24).IsRequired();
            entity.Property(item => item.Control).HasMaxLength(48).IsRequired();
            entity.Property(item => item.ControlDescription).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.Deliverable).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Dependency).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.AppliesWhen).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.LicenseRequirement).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.EstimatedHoursMin).HasPrecision(9, 2);
            entity.Property(item => item.EstimatedHoursMax).HasPrecision(9, 2);
            entity.Property(item => item.DurationDaysMin).HasPrecision(9, 2);
            entity.Property(item => item.DurationDaysMax).HasPrecision(9, 2);
            entity.Property(item => item.ComplexityDriver).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.LeadershipNotes).HasMaxLength(2000).IsRequired();
            entity.HasIndex(item => new { item.CatalogVersion, item.Phase, item.Workstream });
        });

        modelBuilder.Entity<SecurePlusBaselineSetting>(entity =>
        {
            entity.ToTable("SecurePlusBaselineSettings");
            entity.HasKey(item => new { item.CatalogVersion, item.Id });
            entity.Property(item => item.Id).HasMaxLength(200);
            entity.Property(item => item.CatalogVersion).HasMaxLength(32).IsRequired();
            entity.Property(item => item.BaselineReference).HasMaxLength(48).IsRequired();
            entity.Property(item => item.Phase).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Workstream).HasMaxLength(160).IsRequired();
            entity.Property(item => item.TaskName).HasMaxLength(240).IsRequired();
            entity.Property(item => item.GroupAbbreviation).HasMaxLength(24).IsRequired();
            entity.Property(item => item.Control).HasMaxLength(48).IsRequired();
            entity.Property(item => item.SettingCategory).HasMaxLength(160).IsRequired();
            entity.Property(item => item.SettingName).HasMaxLength(240).IsRequired();
            entity.Property(item => item.TargetAction).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.ReviewPath).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.DeploymentMethod).HasMaxLength(160).IsRequired();
            entity.Property(item => item.ValidationMethod).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.EvidenceRequired).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.AppliesWhen).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.LicenseRequirement).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.ImplementationMode).HasMaxLength(80).IsRequired();
            entity.Property(item => item.PilotGroup).HasMaxLength(240).IsRequired();
            entity.Property(item => item.Exclusions).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.ExceptionCriteria).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.RollbackMethod).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.ReviewCadence).HasMaxLength(240).IsRequired();
            entity.Property(item => item.SupportOwner).HasMaxLength(240).IsRequired();
            entity.Property(item => item.EngineeringNotes).HasMaxLength(2000).IsRequired();
            entity.HasIndex(item => new { item.CatalogVersion, item.Phase, item.ImplementationMode });
            entity.HasIndex(item => new
            {
                item.BaselineReference,
                item.CatalogVersion,
                item.Control,
                item.SettingCategory,
                item.SettingName
            }).IsUnique();
        });

        modelBuilder.Entity<TargetModelTable>(entity =>
        {
            entity.ToTable("TargetModelTables");
            entity.HasKey(item => new { item.CatalogVersion, item.Id });
            entity.Property(item => item.Id).HasMaxLength(160);
            entity.Property(item => item.CatalogVersion).HasMaxLength(48).IsRequired();
            entity.Property(item => item.Schema).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.EstimatedRows).HasMaxLength(200).IsRequired();
            entity.Property(item => item.PrimarySources).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.TemporalHistory).HasMaxLength(24).IsRequired();
            entity.HasIndex(item => new { item.CatalogVersion, item.Schema, item.Name }).IsUnique();
            entity.HasMany(item => item.Columns)
                .WithOne(column => column.Table)
                .HasForeignKey(column => new { column.CatalogVersion, column.TableId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TargetModelColumn>(entity =>
        {
            entity.ToTable("TargetModelColumns");
            entity.HasKey(item => new { item.CatalogVersion, item.TableId, item.ColumnOrdinal });
            entity.Property(item => item.TableId).HasMaxLength(160);
            entity.Property(item => item.CatalogVersion).HasMaxLength(48).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(128).IsRequired();
            entity.Property(item => item.DataType).HasMaxLength(80).IsRequired();
            entity.Property(item => item.ForeignKeyTarget).HasMaxLength(200);
            entity.HasIndex(item => new { item.CatalogVersion, item.TableId, item.Name }).IsUnique();
        });

        modelBuilder.Entity<StandardsReferenceSource>(entity =>
        {
            entity.ToTable("StandardsReferenceSources");
            entity.HasKey(item => new { item.CatalogVersion, item.Id });
            entity.Property(item => item.Id).HasMaxLength(80);
            entity.Property(item => item.CatalogVersion).HasMaxLength(48).IsRequired();
            entity.Property(item => item.Area).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(240).IsRequired();
            entity.Property(item => item.FileName).HasMaxLength(300).IsRequired();
            entity.Property(item => item.Format).HasMaxLength(40).IsRequired();
            entity.Property(item => item.SourceVersion).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(80).IsRequired();
            entity.Property(item => item.UsageBoundary).HasMaxLength(400).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.RecordUnit).HasMaxLength(80);
            entity.HasIndex(item => new { item.CatalogVersion, item.Area, item.Status });
            entity.HasIndex(item => new { item.CatalogVersion, item.Name }).IsUnique();
        });
    }
}
