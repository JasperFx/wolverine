using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-4700 against RabbitMQ, the twin of the PostgreSQL fixture in <c>PostgresqlTests</c>.
///
/// <para>
/// Worth having separately rather than trusting one transport. The companion local queue is created by
/// <c>GlobalPartitionedMessageTopology</c>, which is transport-agnostic, but the two halves of the fix are
/// not obviously so: the reverse map depends on the external slot carrying a resolvable
/// <c>GlobalPartitionLocalQueueUri</c>, and the forward depends on this node being able to build a sending
/// agent for a slot whose <em>listener</em> it has just stopped. On a broker transport that means a
/// connection that is still open for publishing while consumption is shut down, which is a genuinely
/// different situation from the PostgreSQL queue tables.
/// </para>
///
/// <para>
/// Note that this only applies to a topology with companion local queues. A native-ack topology never gets
/// one -- <c>GlobalPartitionedRoute</c> skips the local shortcut entirely and the topology leaves
/// <c>GlobalPartitionLocalQueueUri</c> null -- so the defect cannot arise there. That is asserted below
/// rather than assumed.
/// </para>
/// </summary>
public class Bug_4700_partition_slot_ownership_at_promotion : IAsyncLifetime
{
    private const string BaseName = "rslot4700";

    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;

    public async ValueTask InitializeAsync()
    {
        RabbitSlotRetryHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(RabbitSlotRetryHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "rslot4700";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.MessagePartitioning.ByMessage<RabbitSlotRetryMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedRabbitQueues(BaseName, 2);
                    topology.Message<RabbitSlotRetryMessage>();
                });
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        _runtime = _host.GetRuntime();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private Endpoint[] theSlots()
    {
        return Enumerable.Range(1, 2)
            .Select(i => _runtime.Endpoints.EndpointFor($"rabbitmq://queue/{BaseName}{i}".ToUri())!)
            .ToArray();
    }

    private Envelope promotedEnvelope(RabbitSlotRetryMessage message, Uri parkedAt)
    {
        // Shaped the way the scheduled poller's envelopes are -- read back out of the inbox, so they already
        // carry a message type and a serializer. BatchedSender only serializes when Serializer is set, so a
        // hand-built envelope is forwarded with no payload and quietly never arrives.
        var serializer = _runtime.Options.DefaultSerializer;

        return new Envelope(message)
        {
            Destination = parkedAt,
            MessageType = typeof(RabbitSlotRetryMessage).ToMessageTypeName(),
            Serializer = serializer,
            ContentType = serializer.ContentType
        };
    }

    [Fact]
    public void the_reverse_map_agrees_with_what_the_topology_stamps()
    {
        var slots = theSlots();
        slots.Length.ShouldBe(2);

        foreach (var slot in slots)
        {
            var companion = slot.GlobalPartitionLocalQueueUri
                .ShouldNotBeNull("A Rabbit MQ sharded-queue topology is supposed to get companion local queues");

            _runtime.Endpoints.GlobalPartitionSlotFor(companion).ShouldBe(slot.Uri);
        }
    }

    [Fact]
    public void an_address_that_is_not_a_companion_queue_maps_to_nothing()
    {
        _runtime.Endpoints.GlobalPartitionSlotFor(new Uri("local://something-else/")).ShouldBeNull();
        _runtime.Endpoints.GlobalPartitionSlotFor(theSlots()[0].Uri).ShouldBeNull();
    }

    [Fact]
    public async Task the_slot_owner_runs_a_promoted_envelope_itself()
    {
        var slot = theSlots()[0];
        var companion = slot.GlobalPartitionLocalQueueUri!;

        _runtime.Endpoints.FindListeningAgent(slot.Uri)?.Status
            .ShouldBe(ListeningStatus.Accepting, "Precondition: a Solo host owns every slot");

        var message = new RabbitSlotRetryMessage(Guid.NewGuid());
        var envelope = promotedEnvelope(message, companion);

        await _runtime.EnqueueDirectlyAsync([envelope]);

        await waitForAsync(() => RabbitSlotRetryHandler.Received.Any(x => x.Id == message.Id));

        RabbitSlotRetryHandler.Received.ShouldContain(x => x.Id == message.Id && x.Destination == companion);
    }

    [Fact]
    public async Task a_non_owner_forwards_to_the_slot_and_the_message_still_arrives()
    {
        var slot = theSlots()[0];
        var companion = slot.GlobalPartitionLocalQueueUri!;

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        _runtime.Endpoints.FindListeningAgent(slot.Uri)?.Status
            .ShouldNotBe(ListeningStatus.Accepting, "Precondition: this node must NOT own the slot");

        var message = new RabbitSlotRetryMessage(Guid.NewGuid());
        var envelope = promotedEnvelope(message, companion);

        await _runtime.EnqueueDirectlyAsync([envelope]);

        envelope.Destination.ShouldBe(slot.Uri);
        RabbitSlotRetryHandler.Received.ShouldNotContain(x => x.Id == message.Id);

        // Publishing had to keep working while consumption was stopped -- the half that is genuinely
        // different on a broker transport than on the PostgreSQL queue tables.
        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        await waitForAsync(() => RabbitSlotRetryHandler.Received.Any(x => x.Id == message.Id));

        RabbitSlotRetryHandler.Received.ShouldContain(x => x.Id == message.Id && x.Destination == slot.Uri);
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException(
            $"Never arrived. Received=[{RabbitSlotRetryHandler.Received.Select(x => $"{x.Id}@{x.Destination}").Join(", ")}]");
    }
}

public record RabbitSlotRetryMessage(Guid Id);

public static class RabbitSlotRetryHandler
{
    public static readonly ConcurrentBag<(Guid Id, Uri? Destination)> Received = new();

    public static void Handle(RabbitSlotRetryMessage message, Envelope envelope) =>
        Received.Add((message.Id, envelope.Destination));
}
