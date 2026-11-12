using System.Diagnostics;
using EngineeringWorkspace.Application.Scheduling;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Scheduling;

/// <summary>
/// Registers the background schedule runner with Windows Task Scheduler (schtasks.exe) as a task of the signed-in user:
/// every 5 minutes it starts the application with <c>--run-schedules</c>, which runs due schedules and exits. It runs
/// only while the user is signed in (no password is stored), so the user's protected tenant secrets stay readable.
/// </summary>
public sealed class WindowsTaskSchedulerHost(ILogger<WindowsTaskSchedulerHost> logger) : IBackgroundScheduleHost
{
    public const string TaskName = @"GDS Secure Plus Agent\Scheduled assessments";
    public const int IntervalMinutes = 5;

    public async Task<BackgroundHostStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return new BackgroundHostStatus(BackgroundHostState.NotSupported, "Background runs need Windows Task Scheduler.");
        var (exit, output) = await RunAsync(["/Query", "/TN", TaskName, "/FO", "LIST"], cancellationToken);
        return exit == 0
            ? new BackgroundHostStatus(BackgroundHostState.Registered,
                $"Registered in Windows Task Scheduler: due schedules run every {IntervalMinutes} minutes while you are signed in, even when Secure Plus Agent is closed.")
            : new BackgroundHostStatus(BackgroundHostState.NotRegistered,
                "Not registered: schedules run only while Secure Plus Agent is open. Register the background runner to run them when it is closed." +
                (string.IsNullOrWhiteSpace(output) ? string.Empty : string.Empty));
    }

    public async Task<BackgroundHostStatus> RegisterAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return new BackgroundHostStatus(BackgroundHostState.NotSupported, "Background runs need Windows Task Scheduler.");
        var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EngineeringWorkspace.Desktop.exe");
        var (exit, output) = await RunAsync(["/Create", "/F", "/TN", TaskName, "/SC", "MINUTE", "/MO", IntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "/TR", $"\"{executable}\" --run-schedules", "/RL", "LIMITED"], cancellationToken);
        if (exit != 0)
        {
            logger.LogWarning("Registering the scheduled task failed ({Exit}): {Output}", exit, output);
            return new BackgroundHostStatus(BackgroundHostState.Error, $"Windows Task Scheduler refused the task: {output.Trim()}");
        }

        logger.LogInformation("Background schedule runner registered as {Task}", TaskName);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<BackgroundHostStatus> UnregisterAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return new BackgroundHostStatus(BackgroundHostState.NotSupported, "Background runs need Windows Task Scheduler.");
        var (exit, output) = await RunAsync(["/Delete", "/F", "/TN", TaskName], cancellationToken);
        if (exit != 0) logger.LogWarning("Removing the scheduled task failed ({Exit}): {Output}", exit, output);
        return await GetStatusAsync(cancellationToken);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("schtasks.exe did not start.");
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, string.IsNullOrWhiteSpace(error) ? output : error);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, exception.Message);
        }
    }
}
