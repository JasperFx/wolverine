using JasperFx;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Runtime.Recurring;
using Xunit;

namespace CoreTests.Runtime.Recurring;

/// <summary>
/// No message store is a SUPPORTED mode for recurring messages, not a refusal: with no cluster to
/// coordinate, "one agent per cluster" is satisfied by starting the agent directly, and
/// occurrences ride the in-memory scheduled model. These pin that mode end to end — the agent
/// actually runs, the pre-scheduled occurrence actually fires carrying the schedule header and the
/// deterministic dedup id, ticks are idempotent while the next occurrence is pending, and the two
/// modes that CANNOT run the agent at all (Serverless / MediatorOnly) refuse the registrations at
/// startup instead of accepting schedules that would silently never fire.
/// </summary>
public class recurring_messages_on_a_storeless_host
{
    [Fact]
    public async Task the_next_occurrence_is_pre_scheduled_and_fires_with_its_identity()
    {
        StorelessRecurringMessageHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The application assembly is process-pinned by whichever host starts FIRST in a
                // test run (GH-3521) — left implicit, another suite's host can pin it elsewhere and
                // this host then never discovers the handlers below, so the occurrence has no route
                // and silently never arrives. Pin it, per the divergence warning's own advice.
                opts.ApplicationAssembly = typeof(recurring_messages_on_a_storeless_host).Assembly;
                // Every five seconds — the minimum legal cadence — so this is a REAL end-to-end
                // run: the agent pre-schedules the next occurrence, the local queue's in-memory
                // scheduler fires it at its time, and the handler receives it, within seconds.
                opts.Schedules.RecurringMessage<StorelessRecurringMessage>("*/5 * * * * *");
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = (WolverineRuntime)host.GetRuntime();

        var agent = runtime.InMemoryRecurringAgent.ShouldBeOfType<RecurringMessageAgent>();
        agent.Status.ShouldBe(AgentStatus.Running);

        // Generous by design: this is the one wall-clock-dependent assertion in the class, and a
        // full CoreTests run keeps ~40 hosts busy — thread-pool starvation near the end of a run
        // has been observed to delay the in-memory scheduler's firing well past the cron instant.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (StorelessRecurringMessageHandler.Last == null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var envelope = StorelessRecurringMessageHandler.Last;
        envelope.ShouldNotBeNull(
            $"No occurrence was handled within 90s; the agent reports {agent.OccurrencesPublished} publish(es)");

        // The occurrence carries its schedule's identity as a header (what the OTel tag reads)...
        envelope.Headers[RecurringMessage.HeaderKey].ShouldBe(nameof(StorelessRecurringMessage));

        // ...and the deterministic occurrence dedup id — identical on any node and after any
        // restart, which is the entire failover-safety story. ScheduledTime itself is cleared by
        // the scheduled machinery at fire time, so the id is ALSO the durable record of which
        // occurrence this was: parse it back and check it names a real firing instant of this
        // cron, at most a minute away.
        var prefix = $"{nameof(StorelessRecurringMessage)}:";
        envelope.DeduplicationId.ShouldNotBeNull();
        envelope.DeduplicationId.ShouldStartWith(prefix);

        var occurrence = DateTimeOffset.Parse(envelope.DeduplicationId![prefix.Length..]);
        (occurrence.UtcDateTime.Second % 5).ShouldBe(0);
        occurrence.Offset.ShouldBe(TimeSpan.Zero);
        Math.Abs((occurrence - DateTimeOffset.UtcNow).TotalSeconds).ShouldBeLessThan(60);
    }

    [Fact]
    public async Task ticks_are_idempotent_while_the_next_occurrence_is_still_pending()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The application assembly is process-pinned by whichever host starts FIRST in a
                // test run (GH-3521) — left implicit, another suite's host can pin it elsewhere and
                // this host then never discovers the handlers below, so the occurrence has no route
                // and silently never arrives. Pin it, per the divergence warning's own advice.
                opts.ApplicationAssembly = typeof(recurring_messages_on_a_storeless_host).Assembly;
                opts.Schedules.RecurringMessage<PendingRecurringMessage>("0 9 * * *");
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = (WolverineRuntime)host.GetRuntime();
        var agent = runtime.InMemoryRecurringAgent.ShouldBeOfType<RecurringMessageAgent>();

        // The startup tick publishes the (daily, hence still-pending) next occurrence once.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (agent.OccurrencesPublished == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        agent.OccurrencesPublished.ShouldBe(1);

        // "Only the NEXT occurrence is ever in flight": extra ticks while it is still pending are
        // pure no-ops, however often the loop wakes.
        await agent.TickAsync(TestContext.Current.CancellationToken);
        await agent.TickAsync(TestContext.Current.CancellationToken);

        agent.OccurrencesPublished.ShouldBe(1);
    }

    [Fact]
    public async Task the_tick_asks_to_sleep_until_the_next_occurrence_bounded_by_the_maximum()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The application assembly is process-pinned by whichever host starts FIRST in a
                // test run (GH-3521) — left implicit, another suite's host can pin it elsewhere and
                // this host then never discovers the handlers below, so the occurrence has no route
                // and silently never arrives. Pin it, per the divergence warning's own advice.
                opts.ApplicationAssembly = typeof(recurring_messages_on_a_storeless_host).Assembly;
                opts.Schedules.RecurringMessage<PendingRecurringMessage>("0 9 * * *");
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = (WolverineRuntime)host.GetRuntime();
        var agent = runtime.InMemoryRecurringAgent.ShouldBeOfType<RecurringMessageAgent>();

        // A daily schedule is always further away than the maximum tick interval, so the loop
        // sleeps the bounded maximum rather than a day — the cap on how stale its picture gets.
        var delay = await agent.TickAsync(TestContext.Current.CancellationToken);
        delay.ShouldBe(agent.MaximumTickInterval);
    }

    [Fact]
    public async Task serverless_mode_refuses_registered_schedules_at_startup()
    {
        // Serverless runs no agents, so a registered schedule would be accepted and then silently
        // never fire — the exact quiet failure the feature exists to avoid. Refusal, not warning.
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Serverless;
                    opts.Schedules.RecurringMessage<PendingRecurringMessage>("0 9 * * *");
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(nameof(PendingRecurringMessage));
        ex.Message.ShouldContain(nameof(DurabilityMode.Serverless));
    }

    [Fact]
    public async Task mediator_only_mode_refuses_registered_schedules_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.MediatorOnly;
                    opts.Schedules.RecurringMessage<PendingRecurringMessage>("0 9 * * *");
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(nameof(DurabilityMode.MediatorOnly));
    }

    [Fact]
    public async Task a_host_with_no_schedules_starts_no_recurring_agent()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync(TestContext.Current.CancellationToken);

        var runtime = (WolverineRuntime)host.GetRuntime();
        runtime.InMemoryRecurringAgent.ShouldBeNull();
    }
}

public class StorelessRecurringMessage;

/// <summary>Never fired in these tests — the pending-occurrence cases need a schedule that stays pending.</summary>
public class PendingRecurringMessage;

public static class StorelessRecurringMessageHandler
{
    public static volatile Envelope? Last;

    public static void Reset()
    {
        Last = null;
    }

    // The occurrence has to route somewhere for the publish to schedule anything at all — an
    // unroutable message is discarded before it ever reaches the in-memory scheduler.
    public static void Handle(StorelessRecurringMessage message, Envelope envelope)
    {
        Last = envelope;
    }
}

public static class PendingRecurringMessageHandler
{
    public static void Handle(PendingRecurringMessage message)
    {
    }
}
