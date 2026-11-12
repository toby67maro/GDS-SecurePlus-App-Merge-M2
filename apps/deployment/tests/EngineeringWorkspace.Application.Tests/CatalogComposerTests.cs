using EngineeringWorkspace.Application.Catalog;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

/// <summary>4.0 beta.2 reload safety: Lead edits are applied on top of the GDS catalogue and survive any GDS catalogue update.</summary>
public sealed class CatalogComposerTests
{
    private static CatalogControl Control(string id, string value = "On") =>
        new(id, id[^3..], CatalogKinds.ConditionalAccess, "Base", "All", $"Policy {id}", "setting", value, "desc", "GDS", ["NIST 800-53 IA-2"], "High", "Low",
            string.Empty, string.Empty, string.Empty, "identity/conditionalAccess/policies", string.Empty, string.Empty, CatalogEvaluations.ConditionalAccess, [], string.Empty,
            ["SET-A"], new Dictionary<string, string>(), "{}");

    private static ControlCatalog Gds(params CatalogControl[] controls) =>
        new("GDS", "2026-11-01", string.Empty, [new CatalogPolicySet("SET-A", "Set A", "A", 1, [], [], controls.Select(control => control.Id).ToArray())], controls)
        {
            ContentHash = "gds"
        };

    private static CatalogControlEdit Edit(string id, string? basedOn, string value, string? baseHash = null, bool hidden = false) =>
        new(id, basedOn, hidden, CatalogControlContent.From(Control(basedOn ?? id, value)) with { Name = $"Edited {id}" }, baseHash, 2, "lead@gds", DateTimeOffset.UtcNow);

    [Fact]
    public void Lead_controls_edits_hidden_controls_and_sets_are_applied_on_top_of_the_GDS_catalogue()
    {
        var v1 = Gds(Control("CA-100"), Control("CA-101"), Control("CA-102"));
        var edits = new CatalogEdits(
            [
                Edit("CA-100", "CA-100", "Report-only", CatalogComposer.Hash(Control("CA-100"))),
                new CatalogControlEdit("CA-101", "CA-101", true, null, null, 1, "lead@gds", DateTimeOffset.UtcNow),
                Edit("L-0001", null, "Block")
            ],
            [new CatalogPolicySetEdit("L-SET-RETAIL", null, false, "Retail stores", "Lead set", 50, ["CA-100", "L-0001"], [], null, 1, "lead@gds", DateTimeOffset.UtcNow)]);

        var composed = CatalogComposer.Compose(v1, edits);

        composed.Find("CA-100")!.Should().Match<CatalogControl>(control => control.Origin == CatalogOrigins.GdsEdited && control.Value == "Report-only" && !control.IsUpdateAvailable);
        composed.Find("CA-101").Should().BeNull("hidden by a Lead");
        composed.Find("CA-102")!.Origin.Should().Be(CatalogOrigins.Gds);
        composed.Find("L-0001")!.Origin.Should().Be(CatalogOrigins.Lead);
        composed.FindPolicySet("L-SET-RETAIL")!.ControlIds.Should().Equal("CA-100", "L-0001");
        composed.FindPolicySet("SET-A")!.ControlIds.Should().Equal("CA-100", "CA-102");
        composed.Find("L-0001")!.PolicySets.Should().Equal("L-SET-RETAIL");
        composed.ContentHash.Should().StartWith("gds+").And.NotBe(v1.ContentHash, "the database copy is refreshed when Lead edits change");
        CatalogComposer.Compose(v1, CatalogEdits.None).Should().BeSameAs(v1);
    }

    [Fact]
    public void A_GDS_catalogue_update_never_drops_Lead_work_and_flags_changed_or_removed_GDS_controls()
    {
        var v1 = Gds(Control("CA-100"), Control("CA-200"));
        var edits = new CatalogEdits(
            [Edit("CA-100", "CA-100", "Report-only", CatalogComposer.Hash(Control("CA-100"))), Edit("CA-200", "CA-200", "On", CatalogComposer.Hash(Control("CA-200"))),
             Edit("L-0001", null, "Block")],
            [new CatalogPolicySetEdit("L-SET-RETAIL", null, false, "Retail stores", "Lead set", 50, ["CA-200", "L-0001"], [], null, 1, "lead@gds", DateTimeOffset.UtcNow)]);

        // The next GDS catalogue changes CA-100 and removes CA-200.
        var v2 = Gds(Control("CA-100", "Changed by GDS"));
        var composed = CatalogComposer.Compose(v2, edits);

        composed.Find("CA-100")!.Should().Match<CatalogControl>(control => control.Value == "Report-only" && control.IsUpdateAvailable, "the Lead's value is kept and the GDS change is flagged");
        composed.Find("CA-200")!.Should().Match<CatalogControl>(control => control.IsRetired && control.Origin == CatalogOrigins.GdsEdited, "an edit of a removed GDS control is kept as retired");
        composed.Find("L-0001").Should().NotBeNull();
        composed.FindPolicySet("L-SET-RETAIL")!.ControlIds.Should().Equal("CA-200", "L-0001");
    }

    [Fact]
    public void Members_that_no_longer_exist_are_reported_as_retired_and_Lead_ids_never_shadow_GDS_controls()
    {
        var v1 = Gds(Control("CA-100"));
        var set = new CatalogPolicySetEdit("L-SET-1", null, false, "Set", "", 1, ["CA-100", "CA-999"], [], null, 1, "lead@gds", DateTimeOffset.UtcNow);
        var composed = CatalogComposer.Compose(v1, new CatalogEdits([Edit("CA-100", null, "Shadow")], [set]));
        composed.Find("CA-100")!.Origin.Should().Be(CatalogOrigins.Gds, "a Lead control with a GDS ID is ignored");
        composed.FindPolicySet("L-SET-1")!.RetiredMembers.Should().Equal("CA-999");
        CatalogComposer.RetiredMembers(set, composed).Should().Equal("CA-999");
    }

    [Fact]
    public void Assess_and_deploy_methods_are_derived_unless_set()
    {
        Control("CA-100").DeployMethod.Should().Be("Graph");
        Control("CA-100").AssessMethod.Should().Be("Graph");
        (Control("CA-100") with { Kind = CatalogKinds.MicrosoftBaseline, Evaluation = CatalogEvaluations.Manual }).Should()
            .Match<CatalogControl>(control => control.DeployMethod == "GPO" && control.AssessMethod == "Manual");
        (Control("CA-100") with { AssessMethodOverride = "PowerShell" }).AssessMethod.Should().Be("PowerShell");
    }
}
