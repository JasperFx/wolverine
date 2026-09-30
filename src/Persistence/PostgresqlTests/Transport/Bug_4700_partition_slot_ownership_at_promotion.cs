using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-4700, against a REAL sharded topology rather than a hand-stamped one.
///
/// <para>
/// The CoreTests twin sets <c>Endpoint.GlobalPartitionLocalQueueUri</c> itself so it can run without a
/// broker, which proves the forwarding logic but not that the reverse lookup agrees with what
/// <c>GlobalPartitionedMessageTopology</c> actually writes. If the topology ever stamped a different URI --
/// a trailing slash, a different queue name -- the lookup would return null, the fix would silently do
/// nothing, and every test that stamps its own value would still pass. That is the hole this file closes.
/// </para>
///
/// <para>
/// A Solo host owns every slot, which gives both sides of the question honestly: the owner path as it
/// normally runs, and a genuine non-owner produced by stopping one slot's exclusive listener rather than by
/// standing up a second node.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class Bug_4700_partition_slot_ownership_at_promotion : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;

    public async ValueTask InitializeAsync()
    {
        SlotRetryHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "slot4700",
                        transportSchema: "slot4700_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SlotRetryHandler));

                opts.MessagePartitioning.ByMessage<SlotRetryMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues("retryslot", 2);
                    topology.Message<SlotRetryMessage>();
                });
            }).StartAsync();

        _runtime = _host.GetRuntime();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// Shaped the way the scheduled poller's envelopes are: read back out of the inbox, so they already
    /// carry a message type and a serializer. A hand-built Envelope has neither, and BatchedSender only
    /// serializes when <c>Serializer</c> is set (BatchedSender.cs:44) -- so an under-built envelope is
    /// forwarded with no payload and quietly never arrives, which is a defect in the test rather than in
    /// the fix.
    /// </summary>
    private Envelope promotedEnvelope(SlotRetryMessage message, Uri parkedAt)
    {
        var serializer = _runtime.Options.DefaultSerializer;

        return new Envelope(message)
        {
            Destination = parkedAt,
            MessageType = typeof(SlotRetryMessage).ToMessageTypeName(),
            Serializer = serializer,
            ContentType = serializer.ContentType
        };
    }

    private PostgresqlQueue[] theSlots()
    {
        var transport = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        return [transport.Queues["retryslot1"], transport.Queues["retryslot2"]];
    }

    /// <summary>
    /// The one that matters most. Every slot's companion address has to resolve back to that slot, using the
    /// URI the topology itself wrote -- not one this test invented.
    /// </summary>
    [Fact]
    public void the_reverse_map_agrees_with_what_the_topology_stamps()
    {
        var slots = theSlots();
        slots.Length.ShouldBe(2);

        foreach (var slot in slots)
        {
            var companion = slot.GlobalPartitionLocalQueueUri
                .ShouldNotBeNull("The topology is supposed to tag every external slot with its companion queue");

            _runtime.Endpoints.GlobalPartitionSlotFor(companion).ShouldBe(slot.Uri);
        }
    }

    [Fact]
    public void an_address_that_is_not_a_companion_queue_maps_to_nothing()
    {
        // An ordinary local queue has to keep taking the listener circuit
        _runtime.Endpoints.GlobalPartitionSlotFor(new Uri("local://something-else/")).ShouldBeNull();

        // And the slot's own external address is not a companion queue either
        _runtime.Endpoints.GlobalPartitionSlotFor(theSlots()[0].Uri).ShouldBeNull();
    }

    /// <summary>
    /// The owner path, and the control that keeps the fix from diverting traffic that was already correct.
    /// This host listens to every slot, so a promoted envelope at a companion address is its to run.
    /// </summary>
    [Fact]
    public async Task the_slot_owner_runs_a_promoted_envelope_itself()
    {
        var slot = theSlots()[0];
        var companion = slot.GlobalPartitionLocalQueueUri!;

        var message = new SlotRetryMessage(Guid.NewGuid());
        var envelope = promotedEnvelope(message, companion);

        var agent = _runtime.Endpoints.FindListeningAgent(slot.Uri);
        var diagnostic =
            $"slot={slot.Uri} companion={companion} agent={(agent == null ? "NULL" : agent.Status.ToString())} " +
            $"slotFor={_runtime.Endpoints.GlobalPartitionSlotFor(companion)}";

        await _runtime.EnqueueDirectlyAsync([envelope]);

        await waitForAsync(() => SlotRetryHandler.Received.Any(x => x.Id == message.Id),
            () => diagnostic + $" received=[{SlotRetryHandler.Received.Select(x => $"{x.Id}@{x.Destination}").Join(", ")}]" +
                  $" envelopeDestinationAfter={envelope.Destination}");

        SlotRetryHandler.Received.ShouldContain(x => x.Id == message.Id && x.Destination == companion, diagnostic);
    }

    /// <summary>
    /// The reported defect. With the slot's listener stopped this node is a genuine non-owner, exactly as a
    /// second node would be, and the promoted retry must not run here. It is then handed to the slot queue,
    /// so restarting the listener delivers it -- which is what makes this a forward rather than a drop.
    /// </summary>
    [Fact]
    public async Task a_non_owner_forwards_to_the_slot_and_the_message_still_arrives()
    {
        var slot = theSlots()[0];
        var companion = slot.GlobalPartitionLocalQueueUri!;

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        _runtime.Endpoints.FindListeningAgent(slot.Uri)?.Status
            .ShouldNotBe(ListeningStatus.Accepting, "Precondition: this node must NOT own the slot");

        var message = new SlotRetryMessage(Guid.NewGuid());
        var envelope = promotedEnvelope(message, companion);

        await _runtime.EnqueueDirectlyAsync([envelope]);

        // Re-addressed to the slot, not left at the companion queue where the owner would re-park it
        envelope.Destination.ShouldBe(slot.Uri);

        // Nothing ran here. A non-owner executing this concurrently with the owner, under the same group
        // id, is the entire defect.
        SlotRetryHandler.Received.ShouldNotContain(x => x.Id == message.Id);

        // It was forwarded, not dropped: give the slot back its listener and the message arrives.
        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        await waitForAsync(() => SlotRetryHandler.Received.Any(x => x.Id == message.Id),
            () => $"received=[{SlotRetryHandler.Received.Select(x => $"{x.Id}@{x.Destination}").Join(", ")}]");

        SlotRetryHandler.Received.ShouldContain(x => x.Id == message.Id && x.Destination == slot.Uri);
    }

    private static async Task waitForAsync(Func<bool> condition, Func<string> diagnostic)
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException($"Never arrived. {diagnostic()}");
    }
}

public record SlotRetryMessage(Guid Id);

public static class SlotRetryHandler
{
    public static readonly ConcurrentBag<(Guid Id, Uri? Destination)> Received = new();

    public static void Handle(SlotRetryMessage message, Envelope envelope) =>
        Received.Add((message.Id, envelope.Destination));
}

/// <summary>
/// GH-4700 combined with <c>BatchMessagesOf</c>. Worth its own fixture because GH-3867 part 2 made an
/// assembled batch route to the <em>topology slot</em> for its group rather than to a dedicated local queue
/// of its own -- so on a global partitioned topology a batch takes the same companion-queue shortcut a
/// single message does, and a batched handler failing under a ScheduleRetry policy parks at the same
/// address. The forwarded envelope is a <c>T[]</c> rather than a <c>T</c>, which is the part that could
/// plausibly differ: array message type names have bitten codegen before (GH-3399).
/// </summary>
[Collection("Postgresql")]
public class Bug_4700_partition_slot_forwarding_with_batching : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;

    public async ValueTask InitializeAsync()
    {
        BatchedSlotHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "slot4700b",
                        transportSchema: "slot4700b_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(BatchedSlotHandler));

                opts.MessagePartitioning.ByMessage<BatchedSlotMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues("batchslot", 2);
                    topology.Message<BatchedSlotMessage>();
                });

                opts.BatchMessagesOf<BatchedSlotMessage>(batching =>
                {
                    batching.BatchSize = 5;
                    batching.TriggerTime = 100.Milliseconds();
                });
            }).StartAsync();

        _runtime = _host.GetRuntime();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private PostgresqlQueue theSlot()
    {
        return _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["batchslot1"];
    }

    private PostgresqlQueue[] allSlots()
    {
        var transport = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        return [transport.Queues["batchslot1"], transport.Queues["batchslot2"]];
    }

    [Fact]
    public void the_batching_queue_is_not_mistaken_for_a_companion_queue()
    {
        // Batching introduces local queues of its own. Diverting one of those to a partition slot would
        // break batching outright, so the reverse map has to stay exact.
        var companions = allSlots().Select(x => x.GlobalPartitionLocalQueueUri).ToHashSet();
        companions.ShouldNotContain((Uri?)null, "Every slot should have been tagged with its companion");

        foreach (var local in _runtime.Options.Transports.AllEndpoints()
                     .Where(x => x.Uri.Scheme == TransportConstants.Local)
                     .Where(x => !companions.Contains(x.Uri)))
        {
            _runtime.Endpoints.GlobalPartitionSlotFor(local.Uri)
                .ShouldBeNull($"{local.Uri} is not a global partition companion queue");
        }

        // ... and every real companion still resolves to its own slot
        foreach (var slot in allSlots())
        {
            _runtime.Endpoints.GlobalPartitionSlotFor(slot.GlobalPartitionLocalQueueUri!).ShouldBe(slot.Uri);
        }
    }

    [Fact]
    public async Task a_promoted_batch_is_forwarded_to_the_slot_and_still_arrives()
    {
        var slot = theSlot();
        var companion = slot.GlobalPartitionLocalQueueUri!;

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        var batch = new[] { new BatchedSlotMessage(id), new BatchedSlotMessage(id) };

        var serializer = _runtime.Options.DefaultSerializer;
        var envelope = new Envelope(batch)
        {
            Destination = companion,
            MessageType = typeof(BatchedSlotMessage[]).ToMessageTypeName(),
            Serializer = serializer,
            ContentType = serializer.ContentType
        };

        await _runtime.EnqueueDirectlyAsync([envelope]);

        envelope.Destination.ShouldBe(slot.Uri);
        BatchedSlotHandler.Received.ShouldNotContain(x => x.Id == id);

        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline && BatchedSlotHandler.Received.Count(x => x.Id == id) < 2)
        {
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        BatchedSlotHandler.Received.Count(x => x.Id == id)
            .ShouldBe(2, $"Both batch members arrive. Received=[{BatchedSlotHandler.Received.Select(x => x.Id.ToString()).Join(", ")}]");
    }
}

public record BatchedSlotMessage(Guid Id);

public static class BatchedSlotHandler
{
    public static readonly ConcurrentBag<BatchedSlotMessage> Received = new();

    public static void Handle(BatchedSlotMessage[] messages)
    {
        foreach (var message in messages) Received.Add(message);
    }
}
