using EngineeringWorkspace.Application.Assessments;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

public sealed class ConcurrencyTests
{
    [Theory]
    [InlineData(5, 5, false)]
    [InlineData(1, 1, false)]
    [InlineData(20, 20, false)]
    [InlineData(21, 20, true)]
    [InlineData(500, 20, true)]
    [InlineData(0, 1, true)]
    [InlineData(-3, 1, true)]
    public void Max_concurrent_assessments_is_clamped_to_1_to_20(int requested, int expected, bool corrected) =>
        ConcurrencyLimit.Clamp(requested).Should().Be((expected, corrected));

    [Fact]
    public async Task Throttled_runner_never_exceeds_the_limit_and_runs_everything()
    {
        var running = 0;
        var peak = 0;
        var done = 0;
        await ThrottledRunner.RunAsync(Enumerable.Range(0, 30), 4, async (_, token) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(10, token);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref done);
        }, CancellationToken.None);
        peak.Should().BeLessThanOrEqualTo(4).And.BeGreaterThan(1);
        done.Should().Be(30);
    }

    [Fact]
    public async Task Cancelling_stops_queued_work_and_cancels_running_work()
    {
        using var cancel = new CancellationTokenSource();
        var started = 0;
        var cancelledWhileRunning = 0;
        var run = ThrottledRunner.RunAsync(Enumerable.Range(0, 10), 2, async (_, token) =>
        {
            Interlocked.Increment(ref started);
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelledWhileRunning);
                throw;
            }
        }, cancel.Token);
        await Task.Delay(50);
        await cancel.CancelAsync();
        await run.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        started.Should().Be(2, "queued items never start after cancellation");
        cancelledWhileRunning.Should().Be(2);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
