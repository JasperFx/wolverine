using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using JasperFx;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Runtime;
using Wolverine.Runtime.Recurring;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Recurring;

/// <summary>
/// GH-5320. Recurring schedules down to a one second cadence on the timer path, and the opt-in
/// <see cref="RecurringMessage.SkipWhileRunning" /> overlap control. The timing tests drive
/// <see cref="RecurringTimerAgent.StepAsync" /> against a settable clock, so nothing here asserts on
/// wall-clock timing except the one end to end test, which only asserts that occurrences arrive.
/// </summary>
public class recurring_every_second_and_skip_while_running
{
    private static RecurringMessageCollection schedules() => new WolverineOptions().Schedules;

    [Theory]
    [InlineData("* * * * * *")]
    [InlineData("*/2 * * * * *")]
    [InlineData("*/4 * * * * *")]
    public void a_schedule_faster_than_the_poller_always_runs_on_a_timer(string cron)
    {
        var collection = schedules();
        var message = collection.ScheduleRecurring("fast", cron, _ => new EverySecondTick());

        message.ExecutionPath.ShouldBe(RecurringExecutionPath.Timer);

        // Even with the automatic timer path turned off: the scheduled-job poller cannot honour it
        collection.TimerThreshold = TimeSpan.Zero;
        message.ExecutionPath.ShouldBe(RecurringExecutionPath.Timer);
    }

    [Fact]
    public void five_seconds_can_still_be_moved_to_the_scheduled_message_path()
    {
        var collection = schedules();
        var message = collection.ScheduleRecurring("five", "*/5 * * * * *", _ => new EverySecondTick());

        collection.TimerThreshold = TimeSpan.Zero;
        message.ExecutionPath.ShouldBe(RecurringExecutionPath.ScheduledMessages);

        collection.TimerThreshold = RecurringMessageCollection.DefaultTimerThreshold;
        message.UseScheduledMessages().ExecutionPath.ShouldBe(RecurringExecutionPath.ScheduledMessages);
    }

    [Fact]
    public void use_scheduled_messages_is_refused_for_a_schedule_faster_than_the_poller()
    {
        var message = schedules().ScheduleRecurring("every-second", "* * * * * *", _ => new EverySecondTick());

        Should.Throw<InvalidOperationException>(() => message.UseScheduledMessages())
            .Message.ShouldContain("cannot honour");

        message.ExecutionPath.ShouldBe(RecurringExecutionPath.Timer);
    }

    [Fact]
    public void configuring_a_one_second_poll_that_never_overlaps()
    {
        var opts = new WolverineOptions();

        #region sample_recurring_every_second_skip_while_running

        // Every second. Anything under 5 seconds always runs on a timer agent.
        // SkipWhileRunning() skips any occurrence that comes due while the
        // previous one is still being handled
        opts.Schedules.ScheduleRecurring("poll-inventory", "* * * * * *",
                occurrence => new PollInventory(occurrence))
            .SkipWhileRunning();

        #endregion

        var message = opts.Schedules.FindByName("poll-inventory")!;
        message.OverlapPolicy.ShouldBe(RecurringOverlapPolicy.SkipWhileRunning);
        message.ExecutionPath.ShouldBe(RecurringExecutionPath.Timer);
    }

    [Fact]
    public void the_overlap_policy_defaults_to_allow()
    {
        schedules().ScheduleRecurring("plain", "* * * * * *", _ => new EverySecondTick())
            .OverlapPolicy.ShouldBe(RecurringOverlapPolicy.Allow);
    }

    [Fact]
    public void skip_while_running_moves_a_long_cadence_schedule_onto_the_timer_path()
    {
        var message = schedules().ScheduleRecurring("daily", "0 9 * * *", _ => new EverySecondTick());
        message.ExecutionPath.ShouldBe(RecurringExecutionPath.ScheduledMessages);

        message.SkipWhileRunning();
        message.ExecutionPath.ShouldBe(RecurringExecutionPath.Timer);
    }

    [Fact]
    public void skip_while_running_and_the_scheduled_message_path_refuse_each_other()
    {
        var scheduled = schedules().ScheduleRecurring("daily", "0 9 * * *", _ => new EverySecondTick())
            .UseScheduledMessages();

        Should.Throw<InvalidOperationException>(() => scheduled.SkipWhileRunning())
            .Message.ShouldContain("only works on the timer path");

        var skipping = schedules().ScheduleRecurring("daily", "0 9 * * *", _ => new EverySecondTick())
            .SkipWhileRunning();

        Should.Throw<InvalidOperationException>(() => skipping.UseScheduledMessages())
            .Message.ShouldContain("only the timer path");
    }

    [Fact]
    public async Task a_one_second_timer_takes_each_occurrence_from_the_cron_and_does_not_drift()
    {
        using var host = await startHostAsync();
        var runtime = host.GetRuntime();

        var message = schedules().ScheduleRecurring("every-second", "* * * * * *", _ => new EverySecondTick());

        var clock = new SettableClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, 300, TimeSpan.Zero));
        var agent = new RecurringTimerAgent(message, runtime, NullLogger.Instance) { TimeProvider = clock };
        var state = new RecurringTimerAgent.LoopState();

        // The first occurrence is the next whole second
        (await agent.StepAsync(state, CancellationToken.None)).ShouldBe(TimeSpan.FromMilliseconds(700));
        state.Pending.ShouldBe(at(12, 0, 1));

        clock.Advance(TimeSpan.FromMilliseconds(700));
        (await agent.StepAsync(state, CancellationToken.None)).ShouldBe(TimeSpan.FromSeconds(1));
        agent.OccurrencesPublished.ShouldBe(1);
        state.Pending.ShouldBe(at(12, 0, 2));

        // The timer wakes 250ms late. The late occurrence fires once, and the wait to the next one is
        // shortened to stay on the cron, not reset to "now + 1 second"
        clock.Advance(TimeSpan.FromMilliseconds(1250));
        (await agent.StepAsync(state, CancellationToken.None)).ShouldBe(TimeSpan.FromMilliseconds(750));
        agent.OccurrencesPublished.ShouldBe(2);
        state.Pending.ShouldBe(at(12, 0, 3));

        clock.Advance(TimeSpan.FromMilliseconds(750));
        (await agent.StepAsync(state, CancellationToken.None)).ShouldBe(TimeSpan.FromSeconds(1));
        agent.OccurrencesPublished.ShouldBe(3);

        // One deduplication id per second, so consecutive occurrences never collapse into each other
        message.DeduplicationIdFor(at(12, 0, 1)).ShouldBe("every-second:2026-01-01T12:00:01.0000000+00:00");
        message.DeduplicationIdFor(at(12, 0, 2)).ShouldNotBe(message.DeduplicationIdFor(at(12, 0, 1)));
    }

    [Fact]
    public async Task skip_while_running_skips_occurrences_while_a_slow_handler_runs_and_resumes_after()
    {
        PollInventoryHandler.Reset();

        using var host = await startHostAsync();
        var runtime = host.GetRuntime();

        var message = schedules()
            .ScheduleRecurring("poll-inventory", "* * * * * *", occurrence => new PollInventory(occurrence))
            .SkipWhileRunning();

        using var meter = new Meter("CoreTests.GH5320");
        var counter = meter.CreateCounter<int>(MetricsConstants.RecurringOccurrencesSkipped);
        var skippedTags = new ConcurrentQueue<object?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == MetricsConstants.ScheduleNameKey) skippedTags.Enqueue(tag.Value);
            }
        });
        listener.Start();

        var clock = new SettableClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, 500, TimeSpan.Zero));
        var agent = new RecurringTimerAgent(message, runtime, NullLogger.Instance, counter) { TimeProvider = clock };
        var state = new RecurringTimerAgent.LoopState();
        var token = TestContext.Current.CancellationToken;

        await agent.StepAsync(state, token);

        // 12:00:01 comes due and starts running. The handler blocks until it is released
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await agent.StepAsync(state, token);
        (await PollInventoryHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), token)).ShouldBe(at(12, 0, 1));

        // 12:00:02 and 12:00:03 come due while it is still running, and are skipped
        clock.Advance(TimeSpan.FromSeconds(1));
        await agent.StepAsync(state, token);
        clock.Advance(TimeSpan.FromSeconds(1));
        await agent.StepAsync(state, token);

        agent.OccurrencesSkipped.ShouldBe(2);
        agent.OccurrencesPublished.ShouldBe(1);
        skippedTags.ShouldBe(["poll-inventory", "poll-inventory"]);

        // The handler finishes...
        PollInventoryHandler.Release.SetResult();
        await state.Running!.WaitAsync(TimeSpan.FromSeconds(30), token);

        // ...and the next occurrence runs. Nothing skipped is back-filled
        clock.Advance(TimeSpan.FromSeconds(1));
        await agent.StepAsync(state, token);
        await state.Running!.WaitAsync(TimeSpan.FromSeconds(30), token);

        agent.OccurrencesPublished.ShouldBe(2);
        agent.OccurrencesSkipped.ShouldBe(2);

        var handled = PollInventoryHandler.Handled.ToArray();
        handled.Select(x => x.Occurrence).ShouldBe([at(12, 0, 1), at(12, 0, 4)]);

        // An invoked occurrence carries the same identity as a published one
        handled[1].DeduplicationId.ShouldBe(message.DeduplicationIdFor(at(12, 0, 4)));
        handled[1].ScheduleName.ShouldBe("poll-inventory");
    }

    [Fact]
    public async Task a_failed_occurrence_does_not_stop_a_skip_while_running_schedule()
    {
        PollInventoryHandler.Reset();
        PollInventoryHandler.Release.SetResult();
        PollInventoryHandler.FailNext = true;

        using var host = await startHostAsync();
        var runtime = host.GetRuntime();

        var message = schedules()
            .ScheduleRecurring("poll-inventory", "* * * * * *", occurrence => new PollInventory(occurrence))
            .SkipWhileRunning();

        var clock = new SettableClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, 500, TimeSpan.Zero));
        var agent = new RecurringTimerAgent(message, runtime, NullLogger.Instance) { TimeProvider = clock };
        var state = new RecurringTimerAgent.LoopState();
        var token = TestContext.Current.CancellationToken;

        await agent.StepAsync(state, token);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await agent.StepAsync(state, token);

        // The invocation's failure is logged, never thrown out of the loop
        await state.Running!.WaitAsync(TimeSpan.FromSeconds(30), token);

        clock.Advance(TimeSpan.FromSeconds(1));
        await agent.StepAsync(state, token);
        await state.Running!.WaitAsync(TimeSpan.FromSeconds(30), token);

        agent.OccurrencesSkipped.ShouldBe(0);
        PollInventoryHandler.Handled.Select(x => x.Occurrence).ShouldBe([at(12, 0, 2)]);
    }

    [Fact]
    public async Task a_one_second_schedule_fires_every_second_on_a_storeless_host()
    {
        EverySecondTickHandler.Reset();

        using var host = await startHostAsync(opts =>
            opts.Schedules.ScheduleRecurring<EverySecondTick>("* * * * * *"));

        var runtime = (WolverineRuntime)host.GetRuntime();
        var agent = runtime.InMemoryRecurringTimers!.AgentFor(RecurringTimerAgentFamily.UriFor(nameof(EverySecondTick)));
        agent.Status.ShouldBe(AgentStatus.Running);

        // Generous on purpose: a full CoreTests run keeps many hosts busy at once. The assertions are
        // about which occurrences arrive, never about how fast
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (EverySecondTickHandler.Occurrences.Count < 3 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var occurrences = EverySecondTickHandler.Occurrences.ToArray();
        occurrences.Length.ShouldBeGreaterThanOrEqualTo(3, "fewer than 3 one-second occurrences were handled within 60s");

        // Each occurrence is a whole second, and no two are the same second
        occurrences.ShouldAllBe(x => x.Millisecond == 0);
        occurrences.Distinct().Count().ShouldBe(occurrences.Length);
    }

    private static DateTimeOffset at(int hour, int minute, int second) =>
        new(2026, 1, 1, hour, minute, second, TimeSpan.Zero);

    private static Task<IHost> startHostAsync(Action<WolverineOptions>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // See recurring_messages_on_a_storeless_host for why the assembly pin matters (GH-3521).
                opts.ApplicationAssembly = typeof(recurring_every_second_and_skip_while_running).Assembly;
                configure?.Invoke(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

public class EverySecondTick;

public static class EverySecondTickHandler
{
    public static ConcurrentQueue<DateTimeOffset> Occurrences { get; private set; } = new();

    public static void Reset() => Occurrences = new();

    public static void Handle(EverySecondTick message, Envelope envelope)
    {
        if (envelope.Headers.TryGetValue(RecurringMessage.OccurrenceHeaderKey, out var raw) && raw != null)
        {
            Occurrences.Enqueue(DateTimeOffset.Parse(raw));
        }
    }
}

public record PollInventory(DateTimeOffset Occurrence);

public static class PollInventoryHandler
{
    public static TaskCompletionSource<DateTimeOffset> Started { get; private set; } = newSource<DateTimeOffset>();
    public static TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static ConcurrentQueue<(DateTimeOffset Occurrence, string? DeduplicationId, string? ScheduleName)> Handled { get; private set; } = new();
    public static bool FailNext { get; set; }

    public static void Reset()
    {
        Started = newSource<DateTimeOffset>();
        Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Handled = new();
        FailNext = false;
    }

    public static async Task Handle(PollInventory message, Envelope envelope, CancellationToken cancellation)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("The external API is down");
        }

        Started.TrySetResult(message.Occurrence);
        await Release.Task.WaitAsync(cancellation);

        envelope.Headers.TryGetValue(RecurringMessage.HeaderKey, out var name);
        Handled.Enqueue((message.Occurrence, envelope.DeduplicationId, name));
    }

    private static TaskCompletionSource<T> newSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
