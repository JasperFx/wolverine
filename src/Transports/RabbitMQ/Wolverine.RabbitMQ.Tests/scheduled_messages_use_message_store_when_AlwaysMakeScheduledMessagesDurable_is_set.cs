using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// Companion to the local-queue case in PostgresqlTests for the
/// <see cref="IPolicies.AlwaysMakeScheduledMessagesDurable"/> policy. Verifies the
/// invariant that scheduled-for-later envelopes destined for a non-durable RabbitMQ
/// sender end up persisted in the message store inbox — both with and without the
/// policy.
///
/// RabbitMQ doesn't support native scheduled sends, so the routing layer
/// (<c>MessageRoute.WriteEnvelope</c>) automatically swaps such envelopes onto the
/// <c>local://durable</c> system queue, which writes to <c>IMessageStore.Inbox</c>.
/// That swap predates the policy, so the policy is effectively a no-op for RabbitMQ
/// — these tests exist to lock that down so a future change to the routing layer
/// can't silently regress durability for scheduled sends to non-native broker
/// transports.
/// </summary>
public class scheduled_messages_use_message_store_when_AlwaysMakeScheduledMessagesDurable_is_set : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private string _queueName = null!;
    private IHost _hostWithPolicy = null!;
    private IHost _hostWithoutPolicy = null!;

    public scheduled_messages_use_message_store_when_AlwaysMakeScheduledMessagesDurable_is_set(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // Distinct queue name per test run so concurrent test runs don't collide.
        _queueName = RabbitTesting.NextQueueName();

        // Two hosts in one fixture so we exercise both code paths against the same queue
        // shape. Each uses its own Postgres schema so scheduled-row counts don't bleed
        // between them. Neither host enables UseDurableInbox/UseDurableOutbox — the rabbit
        // endpoint stays BufferedInMemory, which is the case the policy targets.
        _hostWithPolicy = await buildHostAsync(applyPolicy: true, schemaName: "always_durable_rabbit_on");
        _hostWithoutPolicy = await buildHostAsync(applyPolicy: false, schemaName: "always_durable_rabbit_off");
    }

    private Task<IHost> buildHostAsync(bool applyPolicy, string schemaName)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<DurableScheduledRabbitTestMessage>().ToRabbitQueue(_queueName);
                opts.PublishMessage<DurableScheduledRabbitTestReminder>().ToRabbitQueue(_queueName);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
                opts.Durability.Mode = DurabilityMode.Solo;

                if (applyPolicy)
                {
                    opts.Policies.AlwaysMakeScheduledMessagesDurable();
                }

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_hostWithPolicy is not null)
        {
            await _hostWithPolicy.StopAsync();
            _hostWithPolicy.Dispose();
        }

        if (_hostWithoutPolicy is not null)
        {
            await _hostWithoutPolicy.StopAsync();
            _hostWithoutPolicy.Dispose();
        }
    }

    [Fact]
    public async Task non_durable_rabbit_sender_persists_scheduled_messages_to_message_store_when_policy_is_set()
    {
        var bus = _hostWithPolicy.MessageBus();
        var store = _hostWithPolicy.Services.GetRequiredService<IMessageStore>();

        await bus.ScheduleAsync(new DurableScheduledRabbitTestMessage(Guid.NewGuid()), 5.Minutes());
        await bus.ScheduleAsync(new DurableScheduledRabbitTestReminder(Guid.NewGuid()), 5.Minutes());

        var counts = await pollScheduledCountAsync(store, expected: 2);

        counts.Scheduled.ShouldBe(2);
    }

    [Fact]
    public async Task non_durable_rabbit_sender_already_persists_scheduled_messages_via_routing_swap_without_policy()
    {
        // Lock down the EXISTING routing-layer behavior: even without the policy, scheduled
        // messages destined for a non-native-scheduling broker (RabbitMQ here) end up in
        // the message store via MessageRoute.WriteEnvelope's swap to the local://durable
        // queue. If a future refactor breaks that swap, this test catches it before the
        // policy gets misdiagnosed as the only thing keeping non-native broker scheduling
        // durable.
        var bus = _hostWithoutPolicy.MessageBus();
        var store = _hostWithoutPolicy.Services.GetRequiredService<IMessageStore>();

        await bus.ScheduleAsync(new DurableScheduledRabbitTestMessage(Guid.NewGuid()), 5.Minutes());
        await bus.ScheduleAsync(new DurableScheduledRabbitTestReminder(Guid.NewGuid()), 5.Minutes());

        var counts = await pollScheduledCountAsync(store, expected: 2);

        counts.Scheduled.ShouldBe(2);
    }

    private async Task<PersistedCounts> pollScheduledCountAsync(IMessageStore store, int expected)
    {
        // Storage.Inbox writes are awaited inside the bus's publish path, so by the time
        // bus.ScheduleAsync returns the row should already be in the DB. A short poll
        // guards against background-task timing differences without making the test slow
        // when things are working.
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

public record DurableScheduledRabbitTestMessage(Guid Id);

// TimeoutMessage subtype to mirror saga timeouts. Same path through the bus — confirms
// the policy / routing behavior is not gated on message base type.
public record DurableScheduledRabbitTestReminder(Guid Id) : TimeoutMessage(5.Minutes());
