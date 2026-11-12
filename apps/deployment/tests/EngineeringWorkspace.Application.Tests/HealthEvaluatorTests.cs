using EngineeringWorkspace.Application.Database;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

public sealed class HealthEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static DatabaseHealthSnapshot Snapshot(params VolumeHealth[] volumes) => new()
    {
        CapturedAtUtc = Now, Provider = DatabaseProvider.SqlServer, Server = "sql01", Database = "SecurePlus", HasServerState = true,
        Volumes = volumes, LastFullBackupUtc = Now.AddHours(-2), RecoveryModel = "SIMPLE"
    };

    [Theory]
    [InlineData(20, HealthSeverity.Ok)]
    [InlineData(15, HealthSeverity.Ok)]
    [InlineData(14.9, HealthSeverity.Warning)]
    [InlineData(5, HealthSeverity.Warning)]
    [InlineData(4.9, HealthSeverity.Critical)]
    public void Disk_free_below_15_percent_warns_and_below_5_percent_is_critical(double freePercent, HealthSeverity expected)
    {
        var alerts = HealthEvaluator.Evaluate(Snapshot(new VolumeHealth("D:\\", 100, freePercent)));
        HealthEvaluator.Overall(alerts).Should().Be(expected);
    }

    [Fact]
    public void Missing_server_state_is_information_only_and_offers_the_grant_script()
    {
        var snapshot = Snapshot(new VolumeHealth("D:\\", 100, 50)) with { HasServerState = false, PermissionName = "VIEW SERVER STATE", LoginName = "CONTOSO\\svc-secureplus" };
        var alerts = HealthEvaluator.Evaluate(snapshot);
        alerts.Should().ContainSingle(alert => alert.Area == "Permissions" && alert.Severity == HealthSeverity.Information);
        HealthEvaluator.Overall(alerts).Should().Be(HealthSeverity.Ok);
        HealthEvaluator.GrantScript(snapshot).Should().Contain("GRANT VIEW SERVER STATE TO [CONTOSO\\svc-secureplus];");
    }

    [Fact]
    public void Old_or_missing_backups_are_reported_for_sql_server_but_not_azure_sql()
    {
        HealthEvaluator.Evaluate(Snapshot() with { LastFullBackupUtc = Now.AddDays(-8) }).Should().Contain(alert => alert.Area == "Backup" && alert.Severity == HealthSeverity.Critical);
        HealthEvaluator.Evaluate(Snapshot() with { LastFullBackupUtc = null }).Should().Contain(alert => alert.Area == "Backup" && alert.Severity == HealthSeverity.Warning);
        HealthEvaluator.Evaluate(Snapshot() with { RecoveryModel = "FULL", LastLogBackupUtc = Now.AddHours(-6) }).Should().Contain(alert => alert.Message.Contains("log"));
        HealthEvaluator.Evaluate(Snapshot() with { Provider = DatabaseProvider.AzureSql, BackupsManagedByPlatform = true, LastFullBackupUtc = null })
            .Should().NotContain(alert => alert.Area == "Backup");
    }

    [Fact]
    public void Cpu_log_and_blocking_thresholds()
    {
        var snapshot = Snapshot() with
        {
            SqlCpuPercent = 96,
            Files = [new DatabaseFileHealth("log", "LOG", "L:\\log.ldf", 100, 85, "Unlimited", "64 MB")],
            Blocking = [new BlockingHealth(61, 58, "UPDATE", "LCK_M_X", 400, "SecurePlus")]
        };
        var alerts = HealthEvaluator.Evaluate(snapshot);
        alerts.Should().Contain(alert => alert.Area == "CPU" && alert.Severity == HealthSeverity.Critical);
        alerts.Should().Contain(alert => alert.Area == "Log" && alert.Severity == HealthSeverity.Warning);
        alerts.Should().Contain(alert => alert.Area == "Blocking" && alert.Severity == HealthSeverity.Critical);
    }

    [Fact]
    public void A_connection_error_is_critical_and_nothing_else_is_evaluated()
    {
        var alerts = HealthEvaluator.Evaluate(Snapshot() with { Error = "The database could not be reached." });
        alerts.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical);
    }
}
