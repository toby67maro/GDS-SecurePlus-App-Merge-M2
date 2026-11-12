using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Infrastructure.Persistence;
using FluentAssertions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class CustomerProfileJsonTests
{
    [Fact]
    public void User_groups_and_experience_answers_survive_a_round_trip()
    {
        var profile = CustomerProfile.Create(new CustomerId("contoso"), "Contoso") with
        {
            UserGroups = [new UserGroup("exec", "Executives", "Executive", "Leadership", 12, "EXECUTIVE", new Dictionary<string, string> { ["ID-LOCATIONS"] = "ALLOWLIST" }, "SG-Execs")],
            ExperienceAnswers = new Dictionary<string, string> { ["ID-MFA-METHOD"] = "PASSWORDLESS" }
        };
        var copy = CustomerProfileJson.Deserialize("contoso", CustomerProfileJson.Serialize(profile));
        copy.Groups.Should().ContainSingle().Which.Should().BeEquivalentTo(profile.Groups[0], options => options.Excluding(group => group.Answers).Excluding(group => group.EffectiveAnswers));
        copy.Groups[0].EffectiveAnswers["id-locations"].Should().Be("ALLOWLIST", "answers are case-insensitive");
        copy.DefaultExperienceAnswers["ID-MFA-METHOD"].Should().Be("PASSWORDLESS");

        // Profiles saved by 2.x (no groups) still load.
        var old = CustomerProfileJson.Deserialize("legacy", """{"schema":1,"name":"Legacy","tenants":[],"contacts":[]}""");
        old.Groups.Should().BeEmpty();
        old.DefaultExperienceAnswers.Should().BeEmpty();
    }
}
