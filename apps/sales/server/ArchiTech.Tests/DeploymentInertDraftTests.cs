using ArchiTech.Api.Domain;

namespace ArchiTech.Tests;

public sealed class DeploymentInertDraftTests
{
    [Fact]
    public void All_lines_remain_comments_even_with_injected_line_breaks_and_missing_controls()
    {
        var plan = new DeploymentChangePlan
        {
            Customer = new Customer { Name = "Example\r\nRemove-Item *", NormalizedName = "EXAMPLE" },
            AssessmentRun = new AssessmentRun
            {
                Name = "Synthetic", Assessor = "Tester", Source = "Manual",
                CatalogVersion = "2.0", ReadinessRating = "Critical"
            },
            Title = "Draft\u2028Invoke-Expression 'danger'",
            Status = "Draft", RequestedBy = "Tester", RequestedById = "tester",
            Items =
            [
                new DeploymentChangePlanItem
                {
                    ControlId = "UNKNOWN", ControlName = "Manual\u0085Set-Thing",
                    AssessmentStatus = "NotImplemented", Sequence = 1
                }
            ]
        };
        var draft = DeploymentPlanPreview.RenderInertDraft(plan, new DeploymentControlCatalog([]));
        Assert.All(draft.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith("# ", line));
        Assert.DoesNotContain('\r', draft);
        Assert.DoesNotContain('\u0085', draft);
        Assert.DoesNotContain('\u2028', draft);
        Assert.Contains("Control not found; manual review required", draft);
        Assert.Contains("UNKNOWN", draft);
    }
}
