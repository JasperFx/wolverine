using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
///     GH-4988. The single-node behaviour of BroadcastToAllNodesAsync(). Delivery to the other nodes of a
///     cluster rides each node's control endpoint and is covered by LeadershipElectionCompliance against
///     every message store.
/// </summary>
public class broadcast_to_all_nodes
{
    [Fact]
    public async Task this_node_handles_the_message_exactly_once_on_its_local_queue()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(InvalidateCacheHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var tracked = await host
            .TrackActivity()
            .Timeout(15.Seconds())
            .ExecuteAndWaitAsync(c => c.BroadcastToAllNodesAsync(new InvalidateCache("products")).AsTask());

        tracked.Executed.MessagesOf<InvalidateCache>().Single().Region.ShouldBe("products");
        tracked.Sent.SingleEnvelope<InvalidateCache>().Destination!.Scheme.ShouldBe("local");
    }

    [Fact]
    public async Task never_forwards_to_the_other_endpoints_the_message_type_is_routed_to()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(InvalidateCacheHandler));

                // An explicit route to somewhere else. PublishAsync() would go there, a broadcast must not
                opts.PublishMessage<InvalidateCache>().To("stub://elsewhere");
            }).StartAsync(TestContext.Current.CancellationToken);

        var tracked = await host
            .TrackActivity()
            .Timeout(15.Seconds())
            .ExecuteAndWaitAsync(c => c.BroadcastToAllNodesAsync(new InvalidateCache("orders")).AsTask());

        tracked.Executed.MessagesOf<InvalidateCache>().Single().Region.ShouldBe("orders");

        var sent = tracked.Sent.SingleEnvelope<InvalidateCache>();
        sent.Destination.ShouldBe(new Uri("local://default"));
    }

    [Fact]
    public async Task stamps_correlation_like_every_other_send()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Broadcaster";
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(InvalidateCacheHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var tracked = await host
            .TrackActivity()
            .Timeout(15.Seconds())
            .ExecuteAndWaitAsync(c => c.BroadcastToAllNodesAsync(new InvalidateCache("customers")).AsTask());

        var envelope = tracked.Sent.SingleEnvelope<InvalidateCache>();
        envelope.Source.ShouldBe("Broadcaster");
        envelope.CorrelationId.ShouldNotBeNull();
    }

    [Fact]
    public async Task is_refused_in_mediator_only_mode_like_every_other_messaging_operation()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.MediatorOnly;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(InvalidateCacheHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            host.MessageBus().BroadcastToAllNodesAsync(new InvalidateCache("products")).AsTask());

        ex.Message.ShouldContain(nameof(DurabilityMode.MediatorOnly));
    }

    [Fact]
    public async Task test_message_context_records_the_broadcast()
    {
        var context = new TestMessageContext();

        await context.BroadcastToAllNodesAsync(new InvalidateCache("products"));

        context.BroadcastToAllNodes.Single().ShouldBeOfType<Envelope>()
            .Message.ShouldBe(new InvalidateCache("products"));
        context.AllOutgoing.Count.ShouldBe(1);
        context.Published.ShouldBeEmpty();
        context.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task refuses_a_message_bus_that_is_not_one_of_wolverines_own()
    {
        var bus = Substitute.For<IMessageBus>();

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            bus.BroadcastToAllNodesAsync(new InvalidateCache("products")).AsTask());

        ex.Message.ShouldContain(nameof(TestMessageContext));
    }

    [Fact]
    public void peers_are_every_other_node_that_is_still_heart_beating_and_has_a_control_endpoint()
    {
        var now = DateTimeOffset.UtcNow;
        var staleTime = now.Subtract(1.Minutes());

        var self = node("tcp://self", now);
        var peer = node("tcp://peer", now);
        var anotherPeer = node("tcp://another-peer", staleTime);
        var stale = node("tcp://stale", staleTime.Subtract(1.Seconds()));
        var noControlEndpoint = node(null, now);

        MessageBus.SelectPeerControlUris([self, peer, anotherPeer, stale, noControlEndpoint], self.NodeId, staleTime)
            .ShouldBe([new Uri("tcp://peer"), new Uri("tcp://another-peer")]);
    }

    private static WolverineNode node(string? controlUri, DateTimeOffset lastHealthCheck) => new()
    {
        NodeId = Guid.NewGuid(),
        ControlUri = controlUri?.ToUri(),
        LastHealthCheck = lastHealthCheck
    };
}

#region sample_broadcast_to_all_nodes

public record InvalidateCache(string Region);

public static class InvalidateCacheHandler
{
    // Runs once on every node in the cluster when the message is broadcast
    // with IMessageBus.BroadcastToAllNodesAsync()
    public static void Handle(InvalidateCache message)
    {
        // Evict this node's in-memory copy of the cached region
    }
}

public static class BroadcastingSample
{
    public static async Task CatalogChanged(IMessageBus bus)
    {
        // Every node in the cluster, including this one, gets a copy
        // of the message and handles it once
        await bus.BroadcastToAllNodesAsync(new InvalidateCache("products"));
    }
}

#endregion
