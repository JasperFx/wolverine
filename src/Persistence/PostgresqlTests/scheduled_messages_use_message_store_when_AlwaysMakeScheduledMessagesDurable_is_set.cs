using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Xunit;
using Xunit.Abstractions;

namespace PostgresqlTests;

/// <summary>
/// Verifies <see cref="IPolicies.AlwaysMakeScheduledMessagesDurable"/> for the local-queue
/// path. <c>BufferedLocalQueue.SupportsNativeScheduledSend</c> reports <c>true</c>, but its
/// "native" scheduling is the in-process <c>InMemoryScheduledJobProcessor</c> — non-persistent,
/// lost on host restart. The policy redirects scheduled envelopes destined for non-durable
/// local queues to the message store inbox so they survive crashes and are recovered by the
/// scheduled-job poller.
/// </summary>
public class scheduled_messages_use_message_store_when_AlwaysMakeScheduledMessagesDurable_is_set : PostgresqlContext
{
    private readonly ITestOutputHelper _output;

    public scheduled_messages_use_message_store_when_AlwaysMakeScheduledMessagesDurable_is_set(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task local_queue_persists_scheduled_messages_to_message_store_when_policy_is_set()
    {
        const string schema = "always_durable_on";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
                opts.Durability.Mode = DurabilityMode.Solo;

                // Explicitly non-durable. Without the policy, scheduled envelopes here would
                // sit in the in-process InMemoryScheduledJobProcessor and be lost on restart.
                opts.LocalQueueFor<DurableTimeoutTestMessage>().BufferedInMemory();
                opts.LocalQueueFor<DurableTimeoutTestReminder>().BufferedInMemory();

                opts.Policies.AlwaysMakeScheduledMessagesDurable();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        var bus = host.MessageBus();
        var store = host.Services.GetRequiredService<IMessageStore>();

        // Schedule both a plain message and a TimeoutMessage subtype. The policy is meant to
        // cover both — TimeoutMessage isn't a special case at the scheduling decision point;
        // it was just the original motivating use case (saga timeouts).
        await bus.ScheduleAsync(new DurableTimeoutTestMessage(Guid.NewGuid()), 5.Minutes());
        await bus.ScheduleAsync(new DurableTimeoutTestReminder(Guid.NewGuid()), 5.Minutes());

        var counts = await pollScheduledCountAsync(store, expected: 2);

        counts.Scheduled.ShouldBe(2);
    }

    [Fact]
    public async Task local_queue_uses_in_memory_scheduling_without_the_policy()
    {
        // Negative control: same wiring as the test above MINUS AlwaysMakeScheduledMessagesDurable.
        // BufferedLocalQueue's existing path stays intact — scheduled envelopes go to the
        // in-process scheduler and the store's Scheduled count stays at zero.
        const string schema = "always_durable_off";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.LocalQueueFor<DurableTimeoutTestMessage>().BufferedInMemory();
                opts.LocalQueueFor<DurableTimeoutTestReminder>().BufferedInMemory();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        var bus = host.MessageBus();
        var store = host.Services.GetRequiredService<IMessageStore>();

        await bus.ScheduleAsync(new DurableTimeoutTestMessage(Guid.NewGuid()), 5.Minutes());
        await bus.ScheduleAsync(new DurableTimeoutTestReminder(Guid.NewGuid()), 5.Minutes());

        // Give any async outgoing flush a moment to complete; no scheduled rows should appear
        // because the in-memory scheduler is the destination, not the message store.
        await Task.Delay(500);
        var counts = await store.Admin.FetchCountsAsync();
        counts.Scheduled.ShouldBe(0);
    }

    private async Task<PersistedCounts> pollScheduledCountAsync(IMessageStore store, int expected)
    {
        // The IMessageStore.Inbox.ScheduleExecutionAsync write is awaited inside the publish
        // path, so by the time bus.ScheduleAsync returns the row should already be in the DB.
        // A short poll guards against any background-task timing differences without making
        // the test slow when things are working.
        var sw = Stopwatch.StartNew();
        PersistedCounts counts;
        do
        {
            counts = await store.Admin.FetchCountsAsync();
            _output.WriteLine($"[POLL] Scheduled={counts.Scheduled}, Incoming={counts.Incoming}");
            if (counts.Scheduled >= expected) return counts;
            await Task.Delay(100);
        } while (sw.Elapsed < TimeSpan.FromSeconds(5));

        return counts;
    }
}

public record DurableTimeoutTestMessage(Guid Id);

// TimeoutMessage subtype to mirror the saga-timeout scenario that originally
// motivated the policy. The 5-minute delay is plenty long that neither test
// race-condition fires the handler during the assertion window.
public record DurableTimeoutTestReminder(Guid Id) : TimeoutMessage(5.Minutes());

public static class DurableTimeoutTestHandler
{
    // Handlers exist purely so default local routing routes the messages to a local queue.
    // Both queues are configured BufferedInMemory in the test setup.
    public static void Handle(DurableTimeoutTestMessage _) { }
    public static void Handle(DurableTimeoutTestReminder _) { }
}
