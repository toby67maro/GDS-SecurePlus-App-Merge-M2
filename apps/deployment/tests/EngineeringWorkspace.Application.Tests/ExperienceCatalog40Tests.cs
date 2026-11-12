using EngineeringWorkspace.Application.Experience;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

/// <summary>4.0 experience catalogue: renamed profiles keep their codes, every profile answers every question, new frameworks and areas.</summary>
public sealed class ExperienceCatalog40Tests
{
    [Fact]
    public void Every_profile_answers_every_question_with_a_real_option()
    {
        foreach (var profile in ExperienceCatalog.Profiles)
            foreach (var question in ExperienceCatalog.Questions)
            {
                profile.Answers.Should().ContainKey(question.Code, $"{profile.Name} must answer {question.Code}");
                question.Options.Select(option => option.Code).Should().Contain(profile.Answers[question.Code], $"{profile.Name} / {question.Code}");
            }
    }

    [Fact]
    public void Renamed_profiles_keep_their_codes_and_the_agreed_settings()
    {
        var names = ExperienceCatalog.Profiles.ToDictionary(profile => profile.Code, profile => profile.Name);
        names["FRICTIONLESS"].Should().Be("GDS - Essentials");
        names["BALANCED"].Should().Be("GDS - Balanced (Recommended)");
        names["LOCKEDDOWN"].Should().Be("GDS - Locked Down");
        names["ADMIN"].Should().Be("User Group - Privileged Administrator");
        names["CONTRACTOR"].Should().Be("User Group - Contractor / Vendor");
        names["REGULATED"].Should().Be("User Group - Regulated Data Handler");
        names["REMOTESITE"].Should().Be("User Group - Remote Site");
        ExperienceCatalog.Profile("FRICTIONLESS").Answers["AI-COPILOT"].Should().Be("PILOT");
        ExperienceCatalog.Profile("FRICTIONLESS").Answers["APPS-MACROS"].Should().Be("SIGNED");
        ExperienceCatalog.Profile("DEVELOPER").Answers["APPS-MACROS"].Should().Be("SIGNED");
        ExperienceCatalog.Profile("FRONTLINE").Answers["ID-LOCATIONS"].Should().Be("APPROVEDORCOMPLIANT");
        ExperienceCatalog.Profile("REMOTESITE").Answers["ID-LOCATIONS"].Should().Be("COMPLIANTANYWHERE");
        ExperienceCatalog.Classification("RemoteSite").DefaultProfile.Should().Be("REMOTESITE");
        ExperienceCatalog.Classification("Sales").Name.Should().Be("Sales");
    }

    [Fact]
    public void Frameworks_are_aligned_and_Essential_Eight_is_retired()
    {
        ExperienceCatalog.ActiveFrameworks.Select(framework => framework.Code).Should().Contain(["CISM365L1", "CISM365L2", "NISTCSF2", "GLBA"]).And.NotContain("E8ML2");
        ExperienceCatalog.Framework("E8ML2")!.IsRetired.Should().BeTrue("kept so saved selections still read");
        ExperienceCatalog.Framework("PCI4")!.Name.Should().Be("PCI DSS 4.0.1");
        ExperienceCatalog.Framework("CISIG1")!.Name.Should().Contain("v8.1");
        ExperienceCatalog.Questions.Where(question => question.Solution == "Network Access (Global Secure Access)").Should().HaveCount(3);
        ExperienceCatalog.Questions.SelectMany(question => question.Options).SelectMany(option => option.Frameworks)
            .Should().Contain(note => note.Framework == "GLBA").And.Contain(note => note.Framework == "CISM365L1");
    }

    [Fact]
    public void Shared_device_QR_code_is_an_option_never_a_default_and_never_chosen_automatically()
    {
        var question = ExperienceCatalog.Questions.Single(item => item.Code == "ID-MFA-METHOD");
        var option = question.Options.Single(item => item.Code == ExperienceCatalog.SharedDeviceQrCode);
        option.Label.Should().Contain("not MFA");
        option.UserExperience.Should().Contain("NOT multifactor authentication");
        option.Configuration.Should().Contain("frontline QR code group only").And.Contain("CA-213").And.Contain("CA-214");
        question.DefaultOption.Should().NotBe(ExperienceCatalog.SharedDeviceQrCode);
        ExperienceCatalog.Profiles.Should().OnlyContain(profile => profile.Answers["ID-MFA-METHOD"] != ExperienceCatalog.SharedDeviceQrCode);

        // A group on SMS adjusted for every framework, strict: the engine never moves it to QR code (lowest friction, single-factor).
        var group = new EngineeringWorkspace.Domain.Customers.UserGroup("g1", "Store staff", "Frontline", Answers: new Dictionary<string, string> { ["ID-MFA-METHOD"] = "SMS" });
        var customer = EngineeringWorkspace.Domain.Customers.CustomerProfile.Create(new EngineeringWorkspace.Domain.Common.CustomerId("contoso"), "Contoso") with { UserGroups = [group] };
        foreach (var framework in ExperienceCatalog.Frameworks)
        {
            var result = ExperienceEngine.Evaluate(customer, group, [framework.Code], strict: true);
            result.WithFrameworks.Single(answer => answer.Question.Code == "ID-MFA-METHOD").Option.Code.Should().NotBe(ExperienceCatalog.SharedDeviceQrCode, framework.Code);
            result.Findings.Where(finding => finding.CompliantOption is not null).Should().OnlyContain(finding => finding.CompliantOption!.Code != ExperienceCatalog.SharedDeviceQrCode);
        }

        // Chosen on purpose, it is kept and flagged against MFA frameworks.
        var chosen = group with { Answers = new Dictionary<string, string> { ["ID-MFA-METHOD"] = ExperienceCatalog.SharedDeviceQrCode } };
        ExperienceEngine.Evaluate(customer with { UserGroups = [chosen] }, chosen, ["NIST53M"]).Findings
            .Should().Contain(finding => finding.Option.Code == ExperienceCatalog.SharedDeviceQrCode && finding.Effect == FrameworkEffect.Conflicts);
    }
}
