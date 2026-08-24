using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests.Partitioning;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-3709. <c>ProcessInParallelWithNativeAcks()</c> on a global partitioned topology, exercised end to end
/// against a real broker on hosts with <b>no message store at all</b>.
/// </summary>
/// <remarks>
/// <para><b>The guarantee:</b> no two messages sharing a group id execute concurrently. Within a node the
/// sequential lane inside the slot's own receiver enforces it; across the cluster the exclusive slot listener
/// enforces it, because exactly one node consumes a given slot. Ordering is per-slot best effort, <b>not</b>
/// per-group guaranteed -- redelivery or requeue may reorder, and the ordering unit is the slot rather than
/// the group, so two groups hashing to the same slot serialize against each other.</para>
///
/// <para><b>Why slot ownership is assigned statically here.</b> Wolverine's dynamic one-consumer-per-slot
/// assignment is <c>ExclusiveListenerFamily</c>, which runs under <c>NodeAgentController</c> -- and
/// <c>WolverineRuntime.startAgentsAsync</c> returns early when <c>Storage is NullMessageStore</c>, so a host
/// with no message store never builds a node agent controller and never assigns an exclusive listener. On a
/// storeless host the durability mode therefore has to be Solo, where <c>Endpoint.ShouldAutoStartAsListener</c>
/// starts <i>every</i> listener on <i>every</i> node. Slot ownership in a storage-free cluster is consequently
/// a deployment decision rather than something Wolverine negotiates, and this fixture makes it by stopping the
/// unowned slot listeners on each node. The dynamic-assignment and failover half of the story needs a store
/// for node coordination and lives in <see cref="native_ack_global_partitioning_failover" />.</para>
/// </remarks>
public class native_ack_global_partitioning_cluster : IAsyncLifetime
{
    private readonly List<IHost> _hosts = [];
    private readonly List<(IHost Host, int[] Owned)> _ownership = [];

    public ValueTask InitializeAsync()
    {
        NativeAckPartitionedProcessing.Ledger.Clear();
        NativeAckPartitionedProcessing.Dwell = 50.Milliseconds();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.ToArray())
        {
            try
            {
                await host.StopAsync();
                host.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do about a host that will not shut down cleanly during teardown
            }
        }

        _hosts.Clear();
        _ownership.Clear();
        NativeAckPartitionedProcessing.Ledger.Clear();
    }

    /// <summary>
    /// A host with no message store whatsoever -- no Marten, no Postgres, no inbox. <paramref name="ownedSlots" />
    /// are the zero-based slot indexes this node consumes; every other slot is send-only here.
    /// </summary>
    /// <remarks>
    /// Ownership is applied by stopping the unowned listeners <i>after</i> startup rather than by clearing
    /// <c>IsListener</c> in configuration, for two reasons. It has to be after Compile: every
    /// <c>ListenerConfiguration</c> carries a delayed <c>e.IsListener = true</c> that runs during
    /// <c>Endpoint.Compile()</c>, after endpoint policies, so an <c>IsListener = false</c> set while
    /// configuring is simply overwritten. And a stopped-and-drained listening agent is exactly the state
    /// <c>ExclusiveListenerAgent</c> leaves behind on a node that is not assigned the slot -- so this
    /// reproduces real slot ownership rather than approximating it.
    /// </remarks>
    private async Task<IHost> startStorelessHostAsync(string nodeName, string baseName, int slotCount,
        params int[] ownedSlots)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Storeless hosts have no cluster coordination available, so Solo is the only workable mode.
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRabbitMq("host=localhost;port=5672").AutoProvision().AutoPurgeOnStartup();

                opts.UseNativeAckLetters(nodeName,
                    topology =>
                    {
                        topology.ProcessInParallelWithNativeAcks();
                        topology.UseShardedRabbitQueues(baseName, slotCount);
                    });
            }).StartAsync();

        _hosts.Add(host);
        _ownership.Add((host, ownedSlots));

        var runtime = host.GetRuntime();
        for (var i = 0; i < slotCount; i++)
        {
            if (ownedSlots.Contains(i)) continue;

            var endpoint = runtime.Endpoints.EndpointFor(slotUri(baseName, i))!;
            await runtime.Endpoints.StopListenerAsync(endpoint, CancellationToken.None);
        }

        assertOwnsExactly(host, baseName, slotCount, ownedSlots);

        return host;
    }

    private static Uri slotUri(string baseName, int slotIndex) => new($"rabbitmq://queue/{baseName}{slotIndex + 1}");

    /// <summary>
    /// One consumer per slot is the cluster-wide half of the guarantee, so it is asserted rather than assumed --
    /// both right after ownership is applied and again at the end of the run.
    /// </summary>
    private void assertOwnershipStillHolds(string baseName, int slotCount)
    {
        foreach (var (host, owned) in _ownership)
        {
            assertOwnsExactly(host, baseName, slotCount, owned);
        }
    }

    private static void assertOwnsExactly(IHost host, string baseName, int slotCount, int[] ownedSlots)
    {
        var runtime = host.GetRuntime();

        for (var i = 0; i < slotCount; i++)
        {
            var status = runtime.Endpoints.FindListenerCircuit(slotUri(baseName, i))?.Status;
            var expected = ownedSlots.Contains(i) ? ListeningStatus.Accepting : ListeningStatus.Stopped;

            status.ShouldBe(expected,
                $"{runtime.Options.ServiceName} had slot {baseName}{i + 1} in status {status}, expected {expected}");
        }
    }

    private static void assertSlotsAreNativeAckWithNoCompanionQueue(IHost host, string baseName, int slotCount)
    {
        var runtime = host.GetRuntime();

        for (var i = 1; i <= slotCount; i++)
        {
            var endpoint = runtime.Endpoints.EndpointFor(new Uri($"rabbitmq://queue/{baseName}{i}"))
                .ShouldNotBeNull($"No endpoint was built for slot {baseName}{i}");

            endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
            endpoint.ListenerScope.ShouldBe(ListenerScope.Exclusive);
        }

        // No companion local queues means no bridge and no durable receiver on the path.
        runtime.Endpoints.ActiveSendingAgents()
            .Select(x => x.Destination)
            .Any(x => x.Scheme == "local" && x.Host.StartsWith($"global-{baseName}"))
            .ShouldBeFalse("A native-ack topology must not create companion local queues");
    }

    /// <summary>
    /// The cluster-wide statement of the guarantee: three storage-free nodes, six slots split between them,
    /// and no group id ever executing in two places at once anywhere in the cluster.
    /// </summary>
    [Fact]
    public async Task no_two_messages_of_a_group_execute_concurrently_across_a_storage_free_cluster()
    {
        const string baseName = "naclust";
        const int slotCount = 6;

        var node1 = await startStorelessHostAsync("NativeAckNode1", baseName, slotCount, 0, 1);
        var node2 = await startStorelessHostAsync("NativeAckNode2", baseName, slotCount, 2, 3);
        var node3 = await startStorelessHostAsync("NativeAckNode3", baseName, slotCount, 4, 5);

        assertSlotsAreNativeAckWithNoCompanionQueue(node1, baseName, slotCount);

        var published = await NativeAckPartitionedProcessing.PumpOutLettersAsync(
            [node1.MessageBus, node2.MessageBus, node3.MessageBus], groupCount: 24, messagesPerGroup: 4);

        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(published, 90.Seconds()))
            .ShouldBeTrue("Not every published letter was handled inside the timeout");

        // Nothing restarted a slot listener behind our back, so "exactly one consumer per slot" really did
        // hold for the whole run rather than just at the start of it.
        assertOwnershipStillHolds(baseName, slotCount);

        NativeAckPartitionedProcessing.AssertNoIntraGroupConcurrency();
        NativeAckPartitionedProcessing.AssertEveryLetterWasHandled(published);
        NativeAckPartitionedProcessing.AssertGroupsNeverStraddleSlots();
        NativeAckPartitionedProcessing.AssertEverySlotWasUsed(slotCount);

        // Every node has to have done real work, otherwise "cluster-wide" is a claim about one node.
        NativeAckPartitionedProcessing.Ledger.Handled.Select(x => x.NodeName).Distinct().OrderBy(x => x)
            .ShouldBe(["NativeAckNode1", "NativeAckNode2", "NativeAckNode3"]);
    }

    /// <summary>
    /// The local shortcut hands a message straight to the companion local queue when the publishing node
    /// already owns the target slot. A native-ack topology has no companion queue, and the broker delivery
    /// <i>is</i> the durability story, so the shortcut is disabled: every send goes through the broker even
    /// when this very node is the exclusive consumer of the slot it hashes to.
    /// </summary>
    [Fact]
    public async Task sends_go_through_the_broker_even_when_this_node_owns_every_slot()
    {
        const string baseName = "nashortcut";
        const int slotCount = 3;

        var host = await startStorelessHostAsync("NativeAckSoleNode", baseName, slotCount, 0, 1, 2);

        var published = await NativeAckPartitionedProcessing.PumpOutLettersAsync(
            [host.MessageBus], groupCount: 12, messagesPerGroup: 3);

        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(published, 60.Seconds()))
            .ShouldBeTrue("Not every published letter was handled inside the timeout");

        var destinations = NativeAckPartitionedProcessing.Ledger.Handled
            .Select(x => x.Destination).Distinct().ToArray();

        destinations.ShouldAllBe(x => x!.Scheme == "rabbitmq");

        // The durable topology's tell-tale is local://global-<base>N/ destinations. There must be none.
        destinations.Any(x => x!.Scheme == "local")
            .ShouldBeFalse("The local shortcut fired: " + destinations.Select(x => x!.ToString()).Join(", "));

        NativeAckPartitionedProcessing.AssertNoIntraGroupConcurrency();
        NativeAckPartitionedProcessing.AssertEveryLetterWasHandled(published);
    }
}
