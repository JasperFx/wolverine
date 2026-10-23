using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Runtime.Recurring;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Recurring;

/// <summary>
/// GH-5168. Short-cadence recurring schedules run on a timer agent of their own instead of being
/// pre-scheduled through the scheduled-message machinery. These pin which schedules qualify, the
/// agent identity, the occurrence identity the timer path shares with the scheduled path, and the
/// pause / resume / trigger behaviour on a host with no message store. The clustered half (agent
/// assignment, no double fire across nodes, pause through agent restrictions) lives in
/// PostgresqlTests.
/// </summary>
public class recurring_timer_schedules
{
    private static RecurringMessageCollection schedules() => new WolverineOptions().Schedules;

    [Theory]
    [InlineData("*/5 * * * * *", RecurringExecutionPath.Timer)]
    [InlineData("*/10 * * * *", RecurringExecutionPath.Timer)]
    [InlineData("*/14 * * * *", RecurringExecutionPath.Timer)]

    // Exactly on the threshold is not UNDER it
    [InlineData("*/15 * * * *", RecurringExecutionPath.ScheduledMessages)]
    [InlineData("0 * * * *", RecurringExecutionPath.ScheduledMessages)]
    [InlineData("0 9 * * *", RecurringExecutionPath.ScheduledMessages)]

    // A week is sampled, not the first gap: only busy during weekday working hours, and still a
    // five-minute schedule
    [InlineData("*/5 9-17 * * 1-5", RecurringExecutionPath.Timer)]
    public void schedules_under_the_timer_threshold_run_on_a_timer(string cron, RecurringExecutionPath expected)
    {
        var message = schedules().ScheduleRecurring("sample", cron, _ => new TimerTickMessage());
        message.ExecutionPath.ShouldBe(expected);
    }

    [Fact]
    public void the_minimum_gap_is_sampled_over_a_week_of_occurrences()
    {
        new CronSchedule("0 9,17 * * *").MinimumGapOverAWeek().ShouldBe(TimeSpan.FromHours(8));
        new CronSchedule("*/5 9-17 * * 1-5").MinimumGapOverAWeek().ShouldBe(TimeSpan.FromMinutes(5));
        new CronSchedule("*/5 * * * * *").MinimumGapOverAWeek().ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void the_threshold_is_configurable()
    {
        var collection = schedules();
        var hourly = collection.ScheduleRecurring("hourly", "0 * * * *", _ => new TimerTickMessage());
        var fast = collection.ScheduleRecurring("fast", "*/5 * * * * *", _ => new TimerTickMessage());

        collection.TimerThreshold.ShouldBe(TimeSpan.FromMinutes(15));

        collection.TimerThreshold = TimeSpan.FromHours(2);
        hourly.ExecutionPath.ShouldBe(RecurringExecutionPath.Timer);

        // Zero turns the automatic timer path off altogether
        collection.TimerThreshold = TimeSpan.Zero;
        fast.ExecutionPath.ShouldBe(RecurringExecutionPath.ScheduledMessages);
        hourly.ExecutionPath.ShouldBe(RecurringExecutionPath.ScheduledMessages);
    }

    [Fact]
    public void a_per_schedule_override_wins_in_both_directions()
    {
        var opts = new WolverineOptions();

        #region sample_overriding_the_recurring_execution_path

        // Anything under this runs on a timer agent automatically. 15 minutes is the default
        opts.Schedules.TimerThreshold = TimeSpan.FromMinutes(15);

        // A daily schedule that should still fire the moment it comes due
        opts.Schedules.ScheduleRecurring("daily-timer", "0 9 * * *", _ => new TimerTickMessage())
            .UseTimer();

        // A fast schedule that should stay on the scheduled-message path
        opts.Schedules.ScheduleRecurring("fast-scheduled", "*/30 * * * * *", _ => new TimerTickMessage())
            .UseScheduledMessages();

        #endregion

        opts.Schedules.FindByName("daily-timer")!.ExecutionPath.ShouldBe(RecurringExecutionPath.Timer);
        opts.Schedules.FindByName("fast-scheduled")!.ExecutionPath.ShouldBe(RecurringExecutionPath.ScheduledMessages);

        // Overrides do not move with the threshold
        opts.Schedules.TimerThreshold = TimeSpan.FromDays(7);
        opts.Schedules.FindByName("fast-scheduled")!.ExecutionPath.ShouldBe(RecurringExecutionPath.ScheduledMessages);
    }

    [Fact]
    public void timer_agent_uris_carry_the_schedule_name_exactly()
    {
        var collection = schedules();
        var odd = collection.ScheduleRecurring("Billing/Sweep: EU?", "*/5 * * * * *", _ => new TimerTickMessage());
        var slow = collection.ScheduleRecurring("slow", "0 9 * * *", _ => new TimerTickMessage());

        var uri = odd.TimerAgentUri.ShouldNotBeNull();
        uri.Scheme.ShouldBe(RecurringTimerAgentFamily.SchemeName);

        // URL-encoded into the path, so case and punctuation survive the round trip and the URI
        // never renders as a bare "/"
        RecurringTimerAgentFamily.ScheduleNameFor(uri).ShouldBe("Billing/Sweep: EU?");
        RecurringTimerAgentFamily.ScheduleNameFor(new Uri(uri.ToString())).ShouldBe("Billing/Sweep: EU?");
        uri.AbsolutePath.TrimStart('/').ShouldNotBeEmpty();

        slow.TimerAgentUri.ShouldBeNull();
    }

    [Fact]
    public async Task a_timer_occurrence_is_published_immediately_with_the_scheduled_paths_identity()
    {
        using var host = await startHostAsync(opts =>
            opts.Schedules.ScheduleRecurring("daily-timer", "0 9 * * *", _ => new TimerTickMessage()).UseTimer());

        var runtime = host.GetRuntime();
        var message = runtime.Options.Schedules.FindByName("daily-timer")!;

        // A private agent on a controlled clock, beside the real one (which waits for 9am)
        var clock = new SettableClock(new DateTimeOffset(2026, 1, 1, 8, 59, 58, TimeSpan.Zero));
        var agent = new RecurringTimerAgent(message, runtime, NullLogger.Instance) { TimeProvider = clock };
        var state = new RecurringTimerAgent.LoopState();

        // Not due yet: nothing published, and the wait is the time until the occurrence
        (await agent.StepAsync(state, CancellationToken.None)).ShouldBe(TimeSpan.FromSeconds(2));
        agent.OccurrencesPublished.ShouldBe(0);

        clock.Advance(TimeSpan.FromSeconds(2));

        var tracked = await host.TrackActivity().IncludeExternalTransports()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => agent.StepAsync(state, CancellationToken.None)));

        agent.OccurrencesPublished.ShouldBe(1);

        var occurrence = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var envelope = tracked.Executed.SingleEnvelope<TimerTickMessage>();

        // The same deduplication id and headers the scheduled path stamps, so a rolling deploy or a
        // double run during reassignment collapses to one handling...
        envelope.DeduplicationId.ShouldBe(message.DeduplicationIdFor(occurrence));
        envelope.DeduplicationId.ShouldBe("daily-timer:2026-01-01T09:00:00.0000000+00:00");
        envelope.Headers[RecurringMessage.HeaderKey].ShouldBe("daily-timer");
        DateTimeOffset.Parse(envelope.Headers[RecurringMessage.OccurrenceHeaderKey]!).ShouldBe(occurrence);

        // ...but published for immediate delivery: no scheduled inbox row, no poller
        envelope.ScheduledTime.ShouldBeNull();
    }

    [Fact]
    public async Task missed_timer_occurrences_are_skipped_not_back_filled()
    {
        using var host = await startHostAsync(opts =>
            opts.Schedules.ScheduleRecurring("daily-timer", "0 9 * * *", _ => new TimerTickMessage()).UseTimer());

        var runtime = host.GetRuntime();
        var message = runtime.Options.Schedules.FindByName("daily-timer")!;

        var clock = new SettableClock(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero).AddSeconds(-1));
        var agent = new RecurringTimerAgent(message, runtime, NullLogger.Instance) { TimeProvider = clock };
        var state = new RecurringTimerAgent.LoopState();

        await agent.StepAsync(state, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await agent.StepAsync(state, CancellationToken.None);
        agent.OccurrencesPublished.ShouldBe(1);

        // The process sleeps through two more occurrences (Jan 2 and Jan 3, 9am)
        clock.Advance(TimeSpan.FromDays(2).Add(TimeSpan.FromHours(1)));

        // The one that was pending fires late, once...
        await agent.StepAsync(state, CancellationToken.None);
        agent.OccurrencesPublished.ShouldBe(2);

        // ...and the next pending occurrence is the first one after now, never Jan 3
        state.Pending.ShouldBe(new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero));
        await agent.StepAsync(state, CancellationToken.None);
        agent.OccurrencesPublished.ShouldBe(2);
    }

    [Fact]
    public async Task a_short_cadence_schedule_fires_through_its_timer_agent_on_a_storeless_host()
    {
        TimerTickMessageHandler.Reset();

        using var host = await startHostAsync(opts =>
            opts.Schedules.ScheduleRecurring<TimerTickMessage>("*/5 * * * * *"));

        var runtime = (WolverineRuntime)host.GetRuntime();

        // No message store, so no agent assignment: the timer agent is started directly
        var timers = runtime.InMemoryRecurringTimers.ShouldNotBeNull();
        var agent = timers.AgentFor(RecurringTimerAgentFamily.UriFor(nameof(TimerTickMessage)));
        agent.Status.ShouldBe(AgentStatus.Running);

        // Generous by design, like the sibling storeless tests: a full CoreTests run keeps many
        // hosts busy at once.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (TimerTickMessageHandler.Handled.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        TimerTickMessageHandler.Handled.TryPeek(out var handled).ShouldBeTrue("no occurrence was handled within 90s");

        var occurrence = DateTimeOffset.Parse(handled.Envelope.Headers[RecurringMessage.OccurrenceHeaderKey]!);
        (occurrence.UtcDateTime.Second % 5).ShouldBe(0);
        handled.Envelope.DeduplicationId.ShouldBe($"{nameof(TimerTickMessage)}:{occurrence:O}");

        // The singular recurring agent leaves a timer schedule alone entirely
        var singular = runtime.InMemoryRecurringAgent.ShouldBeOfType<RecurringMessageAgent>();
        await singular.TickAsync(TestContext.Current.CancellationToken);
        singular.OccurrencesPublished.ShouldBe(0);
    }

    [Fact]
    public async Task pause_resume_and_trigger_a_timer_schedule_on_a_storeless_host()
    {
        using var host = await startHostAsync(opts =>
            opts.Schedules.ScheduleRecurring("ten-minute", "*/10 * * * *", _ => new TimerTickMessage()));

        var runtime = (WolverineRuntime)host.GetRuntime();
        var agent = runtime.InMemoryRecurringTimers!.AgentFor(RecurringTimerAgentFamily.UriFor("ten-minute"));
        agent.Status.ShouldBe(AgentStatus.Running);

        var control = host.Services.GetRequiredService<IRecurringScheduleControl>();

        await control.PauseAsync("ten-minute", TestContext.Current.CancellationToken);
        agent.Status.ShouldBe(AgentStatus.Stopped);

        var paused = await describeAsync(runtime, "ten-minute");
        paused.ExecutionPath.ShouldBe(RecurringExecutionPath.Timer);
        paused.AgentUri.ShouldBe(RecurringTimerAgentFamily.UriFor("ten-minute"));
        paused.Paused.ShouldBeTrue();
        paused.NextOccurrence.ShouldBeNull();

        // Pausing says the schedule must not fire, so "run now" is refused
        await Should.ThrowAsync<RecurringSchedulePausedException>(
            () => control.TriggerAsync("ten-minute", TestContext.Current.CancellationToken));

        await control.ResumeAsync("ten-minute", TestContext.Current.CancellationToken);
        agent.Status.ShouldBe(AgentStatus.Running);

        var resumed = await describeAsync(runtime, "ten-minute");
        resumed.Paused.ShouldBeFalse();
        resumed.NextOccurrence.ShouldNotBeNull();

        // A trigger publishes one manual occurrence straight from the calling node
        var tracked = await host.TrackActivity()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ =>
                control.TriggerAsync("ten-minute", TestContext.Current.CancellationToken)));

        var manual = tracked.Executed.Envelopes()
            .Where(x => x.Message is TimerTickMessage)
            .Single(x => x.DeduplicationId!.StartsWith("ten-minute:manual:"));
        manual.Headers[RecurringMessage.HeaderKey].ShouldBe("ten-minute");
    }

    private static async Task<RecurringScheduleDescriptor> describeAsync(IWolverineRuntime runtime, string name)
    {
        var capabilities = await ServiceCapabilities.ReadFrom(runtime, null, TestContext.Current.CancellationToken);
        return capabilities.RecurringSchedules.Single(x => x.Name == name);
    }

    private static Task<IHost> startHostAsync(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // See recurring_messages_on_a_storeless_host for why the assembly pin matters (GH-3521).
                opts.ApplicationAssembly = typeof(recurring_timer_schedules).Assembly;
                configure(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

public class TimerTickMessage;

public static class TimerTickMessageHandler
{
    public static System.Collections.Concurrent.ConcurrentQueue<(Envelope Envelope, DateTimeOffset At)> Handled
    {
        get;
        private set;
    } = new();

    public static void Reset() => Handled = new();

    public static void Handle(TimerTickMessage message, Envelope envelope)
    {
        Handled.Enqueue((envelope, DateTimeOffset.UtcNow));
    }
}
