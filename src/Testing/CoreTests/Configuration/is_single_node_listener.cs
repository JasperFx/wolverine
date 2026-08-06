using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-3590. The durability agents ask this question on every recovery pass to decide whether a destination's
/// inbox rows are theirs to claim, so it has to be right for every <see cref="ListenerScope"/> — and safe for
/// addresses the node has never heard of.
/// </summary>
public class is_single_node_listener
{
    // Deliberately built inside each test rather than from IAsyncLifetime.InitializeAsync: a Wolverine host
    // created from xUnit's async-lifetime path resolves its calling assembly to "testhost", which pins the
    // process-wide RememberedApplicationAssembly and trips remembered_application_assembly_reuse_warning.
    private static async Task withHostAsync(Action<IHost> assertion)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(PortFinder.GetAvailablePort()).ListenWithStrictOrdering("exclusive-one");
                opts.ListenAtPort(PortFinder.GetAvailablePort()).ListenOnlyAtLeader().Named("leader-one");
                opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("competing-one");

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        assertion(host);
    }

    private static bool isSingleNodeListener(IHost host, string endpointName)
    {
        var endpoints = host.GetRuntime().Endpoints;
        return endpoints.IsSingleNodeListener(endpoints.EndpointByName(endpointName)!.Uri);
    }

    [Fact]
    public Task exclusive_listener_is_a_single_node_listener()
    {
        return withHostAsync(host => isSingleNodeListener(host, "exclusive-one").ShouldBeTrue());
    }

    [Fact]
    public Task leader_pinned_listener_is_a_single_node_listener()
    {
        return withHostAsync(host => isSingleNodeListener(host, "leader-one").ShouldBeTrue());
    }

    [Fact]
    public Task competing_consumers_listener_is_not_a_single_node_listener()
    {
        return withHostAsync(host => isSingleNodeListener(host, "competing-one").ShouldBeFalse());
    }

    [Fact]
    public Task unknown_address_is_not_a_single_node_listener()
    {
        return withHostAsync(host =>
            host.GetRuntime().Endpoints.IsSingleNodeListener(new Uri("tcp://localhost:65001")).ShouldBeFalse());
    }

    /// <summary>
    /// GH-3856. PartitionedMessageTopology forces ListenerScope.Exclusive onto every slot, local queues
    /// included, but a local queue never gets a ListeningAgent and so never starts the
    /// ListenerInboxRecoveryLoop that the GH-3590 carve-out hands recovery to. Answering "true" here left the
    /// dormant inbox rows for these queues owned by nobody at all.
    /// </summary>
    [Fact]
    public async Task partitioned_local_queues_are_not_single_node_listeners()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MessagePartitioning.PublishToPartitionedLocalMessaging("activiteiten", 4, topology =>
                {
                    topology.MessagesImplementing<IPartitionedLocalMessage>();
                    topology.ConfigureQueues(q => q.UseDurableInbox());
                });

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync(TestContext.Current.CancellationToken);

        var endpoints = host.GetRuntime().Endpoints;

        foreach (var name in new[] { "activiteiten1", "activiteiten2", "activiteiten3", "activiteiten4" })
        {
            var queue = (LocalQueue)endpoints.EndpointByName(name)!;

            // The topology really does stamp Exclusive onto the local queue...
            queue.ListenerScope.ShouldBe(ListenerScope.Exclusive);

            // ...and the durability agent must claim its inbox rows anyway.
            queue.IsSingleNodeListener.ShouldBeFalse();
            endpoints.IsSingleNodeListener(queue.Uri).ShouldBeFalse();
        }
    }
}

public interface IPartitionedLocalMessage;

public record PartitionedLocalOne(Guid Id) : IPartitionedLocalMessage;

public static class PartitionedLocalMessageHandler
{
    public static void Handle(PartitionedLocalOne message)
    {
    }
}
