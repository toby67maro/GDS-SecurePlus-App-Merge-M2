using EngineeringWorkspace.Application.Experience;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Planning;
using EngineeringWorkspace.Application.State;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

public sealed class WorkCalendarTests
{
    [Fact]
    public void Effort_hours_become_Monday_to_Friday_dates_of_eight_hours()
    {
        // Saturday 7 November 2026 moves to Monday 9 November.
        var calendar = new WorkCalendar(new DateOnly(2026, 11, 7));
        calendar.Start.Should().Be(new DateOnly(2026, 11, 9));
        calendar.DateAt(0).Should().Be(new DateOnly(2026, 11, 9));
        calendar.DateAt(8).Should().Be(new DateOnly(2026, 11, 10), "hour 8 is the start of the second day");
        calendar.FinishDate(0, 8).Should().Be(new DateOnly(2026, 11, 9), "eight hours fill exactly one day");
        calendar.FinishDate(0, 12).Should().Be(new DateOnly(2026, 11, 10));
        calendar.DateAt(40).Should().Be(new DateOnly(2026, 11, 16), "a full week later skips the weekend");
        calendar.WorkDaysUntil(new DateOnly(2026, 11, 16)).Should().Be(6);
    }

    [Fact]
    public void Scheduler_puts_dependencies_first_then_priority_and_runs_streams_in_parallel()
    {
        var calendar = new WorkCalendar(new DateOnly(2026, 11, 9));
        SchedulableItem[] items =
        [
            new("b", 8, 100, ["a"]),
            new("a", 4, 1, []),
            new("c", 4, 50, []),
        ];
        var serial = WorkScheduler.Schedule(items, calendar).ToDictionary(item => item.Item.Key);
        serial["c"].Order.Should().Be(1, "c has the higher priority among the ready items");
        serial["a"].StartHour.Should().BeLessThan(serial["b"].StartHour, "b depends on a");
        serial["b"].StartHour.Should().BeGreaterThanOrEqualTo(serial["a"].FinishHour);
        serial.Values.Max(item => item.FinishHour).Should().Be(16);

        var parallel = WorkScheduler.Schedule(items, calendar, parallelStreams: 2).ToDictionary(item => item.Item.Key);
        parallel.Values.Max(item => item.FinishHour).Should().Be(12, "a and c run side by side, then b");
        parallel["b"].Level.Should().Be(1);
    }

    [Fact]
    public void Scheduler_breaks_dependency_cycles()
    {
        var scheduled = WorkScheduler.Schedule([new("a", 1, 1, ["b"]), new("b", 1, 1, ["a"])], new WorkCalendar(new DateOnly(2026, 11, 9)));
        scheduled.Should().HaveCount(2);
    }
}

public sealed class ExperienceTests
{
    private static CustomerProfile Customer(params UserGroup[] groups) =>
        CustomerProfile.Create(new CustomerId("contoso"), "Contoso") with { UserGroups = groups };

    [Fact]
    public void Catalog_is_consistent()
    {
        ExperienceCatalog.Questions.Select(question => question.Code).Should().OnlyHaveUniqueItems();
        foreach (var question in ExperienceCatalog.Questions)
        {
            question.Options.Should().Contain(option => option.Code == question.DefaultOption, question.Code);
            ExperienceCatalog.Solutions.Should().Contain(question.Solution);
            foreach (var option in question.Options)
            {
                option.Security.Should().BeInRange(0, 100);
                option.Friction.Should().BeInRange(0, 100);
                option.Frameworks.Select(note => note.Framework).Should().OnlyContain(code => ExperienceCatalog.Framework(code) != null, $"{question.Code}/{option.Code}");
            }
        }

        foreach (var profile in ExperienceCatalog.Profiles)
        foreach (var (question, option) in profile.Answers)
            ExperienceCatalog.Question(question)!.Options.Should().Contain(candidate => candidate.Code == option, $"{profile.Code}: {question}");
        ExperienceCatalog.Classifications.Should().OnlyContain(classification => ExperienceCatalog.Profiles.Any(profile => profile.Code == classification.DefaultProfile));
    }

    [Fact]
    public void Group_answers_override_profile_which_overrides_all_users()
    {
        var group = new UserGroup("exec", "Executives", "Executive", ExperienceProfile: "EXECUTIVE",
            Answers: new Dictionary<string, string> { ["ID-LOCATIONS"] = "ANYWHERE" });
        var customer = Customer(group) with { ExperienceAnswers = new Dictionary<string, string> { ["ID-MFA-METHOD"] = "SMS", ["DEV-USB"] = "BLOCK" } };

        var all = ExperienceEngine.Resolve(customer, null).ToDictionary(answer => answer.Question.Code);
        all["ID-MFA-METHOD"].Option.Code.Should().Be("SMS");
        all["ID-MFA-METHOD"].Source.Should().Be("All Users");

        var exec = ExperienceEngine.Resolve(customer, group).ToDictionary(answer => answer.Question.Code);
        exec["ID-LOCATIONS"].Option.Code.Should().Be("ANYWHERE");
        exec["ID-LOCATIONS"].Source.Should().Be("Group");
        exec["ID-MFA-METHOD"].Option.Code.Should().Be("PHISHRESISTANT", "the Executive profile wins over All Users");
        exec.Should().HaveCount(ExperienceCatalog.Questions.Count);
    }

    [Fact]
    public void Frameworks_show_conflicts_and_the_least_disruptive_fix()
    {
        var group = new UserGroup("travel", "Travelers", "Traveler", ExperienceProfile: "FRICTIONLESS",
            Answers: new Dictionary<string, string> { ["ID-LOCATIONS"] = "ANYWHERE", ["DEV-ENCRYPTION"] = "NONE" });
        var experience = ExperienceEngine.Evaluate(Customer(group), group, ["ITAR", "CMMC2"]);

        experience.Findings.Should().Contain(finding => finding.Question.Code == "ID-LOCATIONS" && finding.Framework.Code == "ITAR" && finding.Effect == FrameworkEffect.Conflicts);
        experience.Conflicts.Should().BeGreaterThan(0);
        var fixedAnswers = experience.WithFrameworks.ToDictionary(answer => answer.Question.Code);
        fixedAnswers["DEV-ENCRYPTION"].Option.Code.Should().Be("SILENT", "silent BitLocker is the least disruptive compliant option");
        fixedAnswers["ID-LOCATIONS"].Option.EffectOn("ITAR").Should().NotBe(FrameworkEffect.Conflicts);
        experience.SecurityWithFrameworks.Should().BeGreaterThan(experience.Security);

        var none = ExperienceEngine.Evaluate(Customer(group), group, []);
        none.Findings.Should().BeEmpty();
        none.WithFrameworks.Select(answer => answer.Option.Code).Should().Equal(none.Answers.Select(answer => answer.Option.Code));
    }

    [Fact]
    public void EaaS_form_results_create_groups_targets_and_answers()
    {
        const string json = """
        {
          "customer": { "name": "Contoso Health", "userCount": 240 },
          "personas": { "selected": ["exec", "office", "it_admin", "contractors", "service"] },
          "experience": { "personas": [ { "persona": "contractors", "device": "byod", "prompts": "daily", "offsite": "web_only", "data": "internal", "travel": "no" } ] },
          "frameworks": { "targets": ["hipaa", "cis", "insurance"] },
          "auth": { "allowedMethods": ["authenticator", "whfb"], "passwordless": "pilot", "sspr": "planned" },
          "session": { "unmanagedFrequency": "1d" },
          "network": { "countryBlock": "block_high_risk" },
          "byod": { "model": "apps_only" },
          "windows": { "bitlocker": "silent", "removableStorage": "block_untrusted", "macros": "block" },
          "data": { "externalSharing": "existing", "labels": "want" },
          "tasks": { "createBreakGlass": true }
        }
        """;
        var result = EaasFormImport.Import(Customer(), json);
        result.Profile.Groups.Select(group => group.Name).Should().BeEquivalentTo(
            ["Executives and Assistants", "Office and Knowledge Workers", "IT Administrators", "Contractors and Temporary Staff"], "service accounts are not a user group");
        var contractors = result.Profile.Groups.Single(group => group.Key == "contractors");
        contractors.EffectiveAnswers["DEV-PERSONAL"].Should().Be("WEBONLY");
        contractors.EffectiveAnswers["ID-SIGNIN-FREQUENCY"].Should().Be("WORKDAY");
        result.Profile.DefaultExperienceAnswers["ID-MFA-METHOD"].Should().Be("PASSWORDLESS");
        result.Profile.DefaultExperienceAnswers["DEV-USB"].Should().Be("APPROVED");
        result.Profile.DefaultExperienceAnswers["FILES-SHARING"].Should().Be("EXISTING");
        result.Profile.WorkspaceSettings["Experience.Frameworks"].Should().Be("HIPAA,CISIG1");
        result.Notes.Should().Contain(note => note.Contains("insurance", StringComparison.Ordinal));

        // Importing twice updates the groups instead of duplicating them.
        EaasFormImport.Import(result.Profile, json).Profile.Groups.Should().HaveCount(4);
    }
}

public sealed class SowTemplateTests
{
    [Fact]
    public void Placeholders_are_filled_and_missing_ones_stay_visible()
    {
        SowTemplates.Fill("Hello {{Customer.Name}}, {{ Unknown.Value }}!", new Dictionary<string, string> { ["Customer.Name"] = "Contoso" })
            .Should().Be("Hello Contoso, [Unknown.Value]!");
        var known = SowTemplates.Placeholders.Select(placeholder => placeholder.Name).ToHashSet();
        foreach (var template in SowTemplates.BuiltIn)
        foreach (var section in template.Sections)
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(section.Content, @"\{\{([A-Za-z.]+)\}\}"))
            known.Should().Contain(match.Groups[1].Value, $"{template.Code}/{section.Code}");
        SowTemplates.ToHtml("SOW <1>", "Contoso & Co", [("1. Overview", "Line <b>")]).Should().Contain("SOW &lt;1&gt;").And.Contain("Line &lt;b&gt;");
    }
}

public sealed class AdvancedModeTests
{
    [Fact]
    public async Task Advanced_mode_needs_the_passcode_and_is_remembered()
    {
        var store = new MemoryState();
        var service = new AdvancedModeService(store);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        (await service.TryEnableAsync("wrong")).Should().BeFalse();
        (await service.TryEnableAsync(string.Empty)).Should().BeFalse();
        service.IsEnabled.Should().BeFalse();
        (await service.TryEnableAsync("310LazerLane")).Should().BeTrue();
        service.IsEnabled.Should().BeTrue();
        store.State.AdvancedModeEnabled.Should().BeTrue();
        changes.Should().Be(1);

        var restarted = new AdvancedModeService(store);
        await restarted.InitializeAsync();
        restarted.IsEnabled.Should().BeTrue();
        await restarted.DisableAsync();
        store.State.AdvancedModeEnabled.Should().BeFalse();
    }

    [Fact]
    public void Advanced_menu_is_hidden_until_advanced_mode_and_settings_is_a_top_menu()
    {
        var definition = new NavigationDefinition(RouteCatalog.DefaultNavigation);
        var routes = new RouteRegistry(RouteCatalog.All);
        var features = new Features.FeatureService(new Features.FeatureSettings(new Dictionary<string, bool>()));
        var hidden = NavigationResolver.Resolve(definition, routes, features, true, true);
        hidden.Select(section => section.Id).Should().Equal("dashboard", "assessment", "deployment", "reporting", "workspace", "settings");
        hidden.Single(section => section.Id == "workspace").Children.Select(child => ((NavigationItem)child).Route)
            .Should().Equal(Routes.CustomerProfiles, Routes.SpecificationProfiles, Routes.TenantAppRegistration, Routes.CustomerExceptions);
        hidden.Single(section => section.Id == "settings").Children.Should().HaveCount(10, "Users & Roles was added in 3.2, Policies and Policy Sets in 4.0 beta.2");
        NavigationResolver.Resolve(definition, routes, features, true, true, advancedMode: true).Select(section => section.Id).Should().EndWith("advanced");
    }

    private sealed class MemoryState : IApplicationStateStore
    {
        public ApplicationState State { get; private set; } = ApplicationState.Empty;
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }
    }
}
