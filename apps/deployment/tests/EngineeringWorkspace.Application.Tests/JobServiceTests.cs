using EngineeringWorkspace.Application.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Application.Tests;

public sealed class JobServiceTests
{
    [Fact]
    public async Task Job_reports_steps_and_completes_with_result()
    {
        using var service = new JobService(TimeProvider.System, NullLogger<JobService>.Instance);
        var finished = new TaskCompletionSource<JobSnapshot>();
        service.JobChanged += (_, snapshot) => { if (snapshot.IsFinished) finished.TrySetResult(snapshot); };

        service.Enqueue("Assess", "Assessment", "contoso", ["Connect", "Collect", "Evaluate"], async context =>
        {
            context.BeginStep("Connect");
            context.BeginStep("Collect", "3 datasets");
            await Task.Delay(10);
            context.CompleteStep("done");
            context.SkipStep("Evaluate", "nothing to evaluate");
            return "ok";
        });

        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        result.State.Should().Be(JobState.Succeeded);
        result.Result.Should().Be("ok");
        result.Progress.Should().Be(100);
        result.Steps.Select(step => step.State).Should().Equal(JobStepState.Succeeded, JobStepState.Succeeded, JobStepState.Skipped);
    }

    [Fact]
    public async Task Failing_job_is_reported_as_failed()
    {
        using var service = new JobService(TimeProvider.System, NullLogger<JobService>.Instance);
        var finished = new TaskCompletionSource<JobSnapshot>();
        service.JobChanged += (_, snapshot) => { if (snapshot.IsFinished) finished.TrySetResult(snapshot); };
        service.Enqueue("Broken", "Test", null, ["Step"], context =>
        {
            context.BeginStep("Step");
            throw new InvalidOperationException("boom");
        });

        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        result.State.Should().Be(JobState.Failed);
        result.Error.Should().Be("boom");
        result.Steps[0].State.Should().Be(JobStepState.Failed);
    }
}
