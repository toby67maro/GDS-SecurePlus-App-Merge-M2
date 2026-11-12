using System.Text.Json;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Planning;
using EngineeringWorkspace.Infrastructure.Catalog;
using FluentAssertions;

namespace EngineeringWorkspace.Infrastructure.Tests;

/// <summary>User decisions of 9 Nov 2026: every CA policy excludes the break-glass group and is deployed report-only, monitored 2–3 weeks.</summary>
public sealed class ConditionalAccessRulesTests
{
    private static readonly ControlCatalog Catalog = EmbeddedCatalogService.Load();

    [Fact]
    public void Every_shipped_Conditional_Access_policy_excludes_the_break_glass_group_and_is_report_only()
    {
        var policies = Catalog.Controls.Where(control => control.Kind == CatalogKinds.ConditionalAccess).ToArray();
        policies.Should().NotBeEmpty();
        policies.Select(BreakGlassRule.Problem).OfType<string>().Should().BeEmpty();
        policies.Should().OnlyContain(control => JsonDocument.Parse(control.Body).RootElement.GetProperty("state").GetString() == ConditionalAccessRollout.ReportOnlyState);
    }

    [Fact]
    public void The_rule_flags_missing_exclusions_and_exempts_the_break_glass_policy_and_workload_identities()
    {
        const string missing = """{"conditions":{"users":{"includeUsers":["All"],"excludeGroups":["<ExclusionTempGroup>"]}}}""";
        BreakGlassRule.Problem("CA-999", missing).Should().Contain("break-glass");
        BreakGlassRule.Problem("CA-999", """{"conditions":{"users":{"includeUsers":["All"],"excludeGroups":["<EmergencyAccessAccountsGroup>"]}}}""").Should().BeNull();
        BreakGlassRule.Problem("CA-110", """{"conditions":{"users":{"includeGroups":["<EmergencyAccessAccountsGroup>"]}}}""").Should().BeNull();
        BreakGlassRule.Problem("CA-700", """{"conditions":{"users":{"includeUsers":["None"]},"clientApplications":{"includeServicePrincipals":["All"]}}}""").Should().BeNull();
    }

    [Fact]
    public void A_staged_task_with_Conditional_Access_cannot_be_completed_before_the_monitoring_period_ends()
    {
        var task = new ProjectTask("t1", "1", "Enable CA", "Build", "Engineer", null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), 1, 8, 0, false, ProjectTaskStatuses.Staged, null, null, [],
            ControlIds: ["CA-100"]);
        var passing = new Dictionary<string, string> { ["CA-100"] = "Pass" };
        var staged = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        TaskRemediationService.CanComplete(task, passing, staged, staged.AddDays(10), includesConditionalAccess: true).CanComplete.Should().BeFalse();
        TaskRemediationService.CanComplete(task, passing, staged, staged.AddDays(ConditionalAccessRollout.MinimumReportOnlyDays), includesConditionalAccess: true)
            .CanComplete.Should().BeTrue();
        TaskRemediationService.CanComplete(task, passing, staged, staged.AddDays(1), includesConditionalAccess: false).CanComplete.Should().BeTrue("Intune has no report-only period");
    }
}
