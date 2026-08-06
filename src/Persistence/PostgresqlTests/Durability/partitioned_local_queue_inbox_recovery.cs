using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;

namespace PostgresqlTests.Durability;

/// <summary>
/// GH-3856. PublishToPartitionedLocalMessaging() stamps ListenerScope.Exclusive onto every one of its durable
/// local queues, and the GH-3590 carve-out then handed inbox recovery for those queues to a
/// ListenerInboxRecoveryLoop that is never constructed for a local queue — LocalQueue.BuildListenerAsync()
/// throws and StartListenersAsync() filters local queues out, so they never get a ListeningAgent at all.
///
/// The result in the field was thousands of envelopes sitting at status = 'Incoming', owner_id = 0 for hours,
/// surviving rolling deploys, because neither recovery path would claim them. This test reproduces exactly
/// that state and asserts that the durability agent — which IS a valid owner here, since a local queue exists
/// on every node — drains it.
/// </summary>
public class partitioned_local_queue_inbox_recovery : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Keep the polling tight so the test doesn't wait out the 5 second default
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "partitioned_local_recovery");

                opts.Discovery.DisableConventionalDiscovery().IncludeType<RecoveredMessageHandler>();

                opts.MessagePartitioning.ByMessage<RecoveredMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.PublishToPartitionedLocalMessaging("activiteiten", 4, topology =>
                {
                    topology.Message<RecoveredMessage>();
                    topology.ConfigureQueues(q => q.UseDurableInbox());
                });
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task durability_agent_recovers_dormant_rows_for_a_partitioned_local_queue()
    {
        using var tracking = RecoveredMessages.Track();

        var runtime = _host.GetRuntime();
        var store = _host.Services.GetRequiredService<IMessageStore>();

        var queue = runtime.Endpoints.EndpointByName("activiteiten3")!;

        // Nothing ever builds a ListeningAgent for a local queue, which is precisely why the durability agent
        // has to be the one to claim these rows.
        runtime.Endpoints.FindListeningAgent(queue.Uri).ShouldBeNull();

        var seeded = await seedDormantMessagesAsync(store, runtime, queue.Uri, 5);
        var expected = seeded.Select(x => x.Id).ToArray();

        var succeeded = await waitForAsync(() => expected.All(tracking.Contains), 30.Seconds());

        succeeded.ShouldBeTrue(
            $"Expected the durability agent to recover all {seeded.Length} dormant inbox rows for the " +
            $"partitioned local queue {queue.Uri}, but only saw {tracking.Count}");

        (await store.LoadPageOfGloballyOwnedIncomingAsync(queue.Uri, 100)).ShouldBeEmpty();
    }

    private static async Task<Envelope[]> seedDormantMessagesAsync(IMessageStore store, IWolverineRuntime runtime,
        Uri destination, int count)
    {
        var serializer = runtime.Options.DefaultSerializer!;

        var envelopes = Enumerable.Range(0, count).Select(i =>
        {
            var id = Guid.NewGuid();
            var envelope = new Envelope(new RecoveredMessage(id, i))
            {
                Id = id,
                Destination = destination,
                Status = EnvelopeStatus.Incoming,
                OwnerId = TransportConstants.AnyNode,
                ContentType = serializer.ContentType,
                MessageType = typeof(RecoveredMessage).ToMessageTypeName(),
                SentAt = DateTimeOffset.UtcNow
            };

            envelope.Data = serializer.Write(envelope);

            return envelope;
        }).ToArray();

        await store.Inbox.StoreIncomingAsync(envelopes);

        return envelopes;
    }

    private static async Task<bool> waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100.Milliseconds());
        }

        return condition();
    }
}
