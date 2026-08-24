using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine.ComplianceTests.Partitioning;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-3709. Slot failover is the one true cross-node concurrency hazard for a native-ack global partitioned
/// topology: when a slot moves nodes, the new owner may start pulling while the old owner still has an
/// in-flight handler for a group in that slot. <c>ExclusiveListenerAgent</c> is supposed to stop and
/// <i>drain</i> the listener before releasing it. This suite verifies that claim under the new mode rather
/// than assuming it.
/// </summary>
/// <remarks>
/// <para><b>The guarantee:</b> no two messages sharing a group id execute concurrently -- within a node the
/// sequential lane enforces it, across the cluster the exclusive slot listener does. Ordering is per-slot
/// best effort, not per-group guaranteed; redelivery or requeue may reorder, and two groups hashing to the
/// same slot serialize against each other.</para>
///
/// <para><b>Why there is a database here at all.</b> The slots themselves stay storage-free -- they are
/// <see cref="EndpointMode.NativeAck" />, so no envelope touches an inbox and no message ever hits the
/// database. Postgres is present only as the cluster's node/agent coordination store, because dynamic
/// one-consumer-per-slot assignment runs through <c>NodeAgentController</c>, which
/// <c>WolverineRuntime.startAgentsAsync</c> skips entirely when there is no message store. The genuinely
/// store-free deployment shape -- static slot ownership per node -- is covered by
/// <see cref="native_ack_global_partitioning_cluster" />.</para>
/// </remarks>
// Deliberately NOT in its own [Collection]. The assembly runs CollectionPerAssembly, so an explicit
// collection attribute would put this class in a SEPARATE collection -- which xUnit then runs in PARALLEL
// with the assembly collection, and native_ack_global_partitioning_cluster writes the same static
// NativeAckPartitionedProcessing.Ledger. That combination produced exactly the cross-class contamination
// you would expect.
public class native_ack_global_partitioning_failover : IAsyncLifetime
{
    private const string SchemaName = "native_ack_gp_failover";
    private const string BaseName = "nafail";
    private const int SlotCount = 4;

    private readonly List<IHost> _hosts = [];
    private readonly ITestOutputHelper _output;

    public native_ack_global_partitioning_failover(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        NativeAckPartitionedProcessing.Ledger.Clear();

        // Wide enough that a handler is genuinely still in flight when its slot is handed off.
        NativeAckPartitionedProcessing.Dwell = 250.Milliseconds();

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
        await conn.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _hosts.Reverse();
        foreach (var host in _hosts.ToArray())
        {
            try
            {
                await shutdownHostAsync(host);
            }
            catch (Exception)
            {
                // Nothing useful to do about a host that will not shut down cleanly during teardown
            }
        }

        _hosts.Clear();
        NativeAckPartitionedProcessing.Ledger.Clear();
        NativeAckPartitionedProcessing.Dwell = 50.Milliseconds();
    }

    private async Task<IHost> startHostAsync(string nodeName)
    {
        var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Balanced;
            opts.Durability.HealthCheckPollingTime = 1.Seconds();
            opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
            opts.Durability.CheckAssignmentPeriod = 1.Seconds();
            opts.Durability.StaleNodeTimeout = 3.Seconds();

            opts.UseRabbitMq("host=localhost;port=5672").EnableWolverineControlQueues().AutoProvision();

            // Node/agent coordination only -- see the class remarks. The slots never touch it.
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

            opts.UseNativeAckLetters(nodeName, topology =>
            {
                topology.ProcessInParallelWithNativeAcks();
                topology.UseShardedRabbitQueues(BaseName, SlotCount);
            });

            opts.Services.AddResourceSetupOnStartup();
        }).StartAsync();

        _hosts.Add(host);

        return host;
    }

    private async Task shutdownHostAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
        _hosts.Remove(host);
    }

    private static Uri slotAgentUri(int slotNumber) =>
        new($"{ExclusiveListenerFamily.SchemeName}://rabbitmq/{BaseName}{slotNumber}");

    private IReadOnlyList<Uri> slotAgentsOn(IHost host)
    {
        var running = host.RunningAgents().ToHashSet();
        return Enumerable.Range(1, SlotCount).Select(slotAgentUri).Where(running.Contains).ToArray();
    }

    /// <summary>
    /// Every slot must be consumed by exactly one node -- never zero, never two.
    /// </summary>
    private bool everySlotOwnedExactlyOnce()
    {
        return Enumerable.Range(1, SlotCount)
            .All(slot => _hosts.Count(h => slotAgentsOn(h).Contains(slotAgentUri(slot))) == 1);
    }

    private async Task waitForFullSlotOwnershipAsync(TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (everySlotOwnedExactlyOnce())
            {
                // Has to hold across a health-check cycle to count as settled rather than mid-flap.
                await Task.Delay(1500.Milliseconds());
                if (everySlotOwnedExactlyOnce()) return;
            }

            await Task.Delay(250.Milliseconds());
        }

        var report = _hosts.Select(h =>
            $"{h.GetRuntime().Options.ServiceName}=[{slotAgentsOn(h).Select(x => x.ToString()).Join(", ")}]").Join("; ");

        throw new TimeoutException($"The {SlotCount} slot listeners never settled one-per-node. Saw: {report}");
    }

    private void assertSlotsAreNativeAck(IHost host)
    {
        var runtime = host.GetRuntime();

        for (var i = 1; i <= SlotCount; i++)
        {
            var endpoint = runtime.Endpoints.EndpointFor(new Uri($"rabbitmq://queue/{BaseName}{i}"))
                .ShouldNotBeNull();

            endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
            endpoint.ListenerScope.ShouldBe(ListenerScope.Exclusive);
        }

        _output.WriteLine($"{runtime.Options.ServiceName}: all {SlotCount} slots are NativeAck + Exclusive");
    }

    /// <summary>
    /// Publish a steady stream of grouped letters until cancelled, so the cluster is genuinely mid-flight
    /// when a node is taken away.
    /// </summary>
    private static Task<(Task Pump, ConcurrentQueue<(string GroupId, int Sequence)> Published)> startPumpAsync(
        IMessageBus bus, int groupCount, CancellationToken token)
    {
        var published = new ConcurrentQueue<(string, int)>();
        var groups = Enumerable.Range(0, groupCount).Select(_ => Guid.NewGuid().ToString()).ToArray();

        var pump = Task.Run(async () =>
        {
            var sequence = 0;
            while (!token.IsCancellationRequested)
            {
                foreach (var groupId in groups)
                {
                    if (token.IsCancellationRequested) return;

                    await bus.PublishAsync(new NativeAckLetter(groupId, sequence));
                    published.Enqueue((groupId, sequence));
                }

                sequence++;
                await Task.Delay(100.Milliseconds(), CancellationToken.None);
            }
        }, CancellationToken.None);

        return Task.FromResult((pump, published));
    }

    /// <summary>
    /// The test the issue calls out as the one that matters: take the node owning a slot away mid-stream and
    /// assert that the slot is reassigned, that processing continues, that no two messages sharing a group id
    /// ever executed concurrently <i>across the handoff</i>, and that nothing was lost.
    /// </summary>
    [Fact]
    public async Task no_intra_group_concurrency_when_a_slot_owner_leaves_mid_stream()
    {
        var leader = await startHostAsync("FailoverNode1");
        await startHostAsync("FailoverNode2");
        await startHostAsync("FailoverNode3");

        (await leader.WaitUntilAssumesLeadershipAsync(30.Seconds()))
            .ShouldBeTrue("The first host never assumed leadership");

        assertSlotsAreNativeAck(leader);

        await waitForFullSlotOwnershipAsync(60.Seconds());

        using var cts = new CancellationTokenSource();
        var (pump, published) = await startPumpAsync(leader.MessageBus(), groupCount: 16, cts.Token);

        // Let real work get under way before pulling a node out from under it.
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        // Take away a non-leader node that is actually consuming slots, so the reassignment is a genuine
        // slot handoff rather than a leadership election.
        var candidates = _hosts.Skip(1).Where(h => slotAgentsOn(h).Any()).ToArray();
        candidates.ShouldNotBeEmpty("No non-leader node was consuming a slot, so there is no handoff to test");

        var victim = candidates[0];
        var orphanedSlots = slotAgentsOn(victim);
        var victimName = victim.GetRuntime().Options.ServiceName;

        _output.WriteLine($"Stopping {victimName}, which owns {orphanedSlots.Select(x => x.ToString()).Join(", ")}");
        orphanedSlots.ShouldNotBeEmpty();

        await shutdownHostAsync(victim);

        await waitForFullSlotOwnershipAsync(90.Seconds());

        foreach (var slot in orphanedSlots)
        {
            _hosts.Count(h => slotAgentsOn(h).Contains(slot))
                .ShouldBe(1, $"Slot {slot} did not land on exactly one survivor");
        }

        // Processing has to keep going on the survivors, not merely resume owning the slots.
        var handledBeforeSettling = NativeAckPartitionedProcessing.Ledger.Handled.Count;
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);
        NativeAckPartitionedProcessing.Ledger.Handled.Count.ShouldBeGreaterThan(handledBeforeSettling,
            "Nothing was processed after the slot handoff");

        await cts.CancelAsync();
        await pump;

        var everything = published.ToArray();
        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(everything, 120.Seconds()))
            .ShouldBeTrue("Not every published letter was handled after the failover");

        _output.WriteLine(
            $"{everything.Length} published, {NativeAckPartitionedProcessing.Ledger.Handled.Count} handled "
            + $"(duplicates are legal in this mode)");

        // The claim under test: exclusive-listener handoff drains in-flight work before releasing the slot,
        // so the new owner never overlaps the old owner on a shared group id.
        NativeAckPartitionedProcessing.AssertNoIntraGroupConcurrency();

        // At-least-once completeness -- the broker, not an inbox, is what makes this true.
        NativeAckPartitionedProcessing.AssertEveryLetterWasHandled(everything);

        // A group hashes to one slot and stays there; a handoff changes which node consumes that slot, never
        // which slot the group belongs to.
        NativeAckPartitionedProcessing.AssertGroupsNeverStraddleSlots();

        // The handoff really did move work between nodes, otherwise nothing above was tested.
        var nodes = NativeAckPartitionedProcessing.Ledger.Handled.Select(x => x.NodeName).Distinct().ToArray();
        nodes.ShouldContain(victimName, "The node that was stopped never handled anything before it left");
        nodes.Length.ShouldBeGreaterThan(1);

        // And specifically: the orphaned slots kept being drained, on a survivor. A survivor can only ever
        // handle one of those slots' messages after the handoff, because before it the victim owned them
        // exclusively -- so this is the assertion that the reassigned slots did real work post-failover.
        var orphanedQueues = orphanedSlots.Select(x => x.Segments.Last()).ToHashSet();
        NativeAckPartitionedProcessing.Ledger.Handled
            .Where(x => x.NodeName != victimName && orphanedQueues.Contains(x.Destination!.Segments.Last()))
            .ShouldNotBeEmpty("No survivor ever processed a message from one of the reassigned slots");
    }

    /// <summary>
    /// The sharper version of the same hazard: move a slot between two nodes that are <b>both still alive</b>,
    /// mid-stream. Nothing here is helped along by a dying process -- the old owner keeps running, so the only
    /// thing standing between the new owner's first pull and the old owner's in-flight handler is
    /// <c>ExclusiveListenerAgent.StopAsync</c> draining the listener before it releases the slot.
    /// </summary>
    [Fact]
    public async Task no_intra_group_concurrency_when_a_live_slot_handoff_moves_the_slot()
    {
        var leader = await startHostAsync("HandoffNode1");
        await startHostAsync("HandoffNode2");
        await startHostAsync("HandoffNode3");

        (await leader.WaitUntilAssumesLeadershipAsync(30.Seconds()))
            .ShouldBeTrue("The first host never assumed leadership");

        await waitForFullSlotOwnershipAsync(60.Seconds());

        using var cts = new CancellationTokenSource();
        var (pump, published) = await startPumpAsync(leader.MessageBus(), groupCount: 16, cts.Token);

        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        var movedSlot = slotAgentUri(1);

        var owners = _hosts.Where(h => slotAgentsOn(h).Contains(movedSlot)).ToArray();
        owners.Length.ShouldBe(1,
            $"{movedSlot} was owned by {owners.Length} nodes rather than exactly one when the handoff started");

        var originalOwner = owners[0];
        var target = _hosts.First(h => !ReferenceEquals(h, originalOwner));

        var originalOwnerName = originalOwner.GetRuntime().Options.ServiceName;
        var targetNumber = target.GetRuntime().DurabilitySettings.AssignedNodeNumber;

        _output.WriteLine(
            $"Moving {movedSlot} from {originalOwnerName} to {target.GetRuntime().Options.ServiceName} "
            + $"(node {targetNumber}) while both are live");

        var restrictions = new AgentRestrictions();
        restrictions.PinAgent(movedSlot, targetNumber);
        await leader.GetRuntime().Agents.ApplyRestrictionsAsync(restrictions, CancellationToken.None);

        await waitForSlotOwnerAsync(movedSlot, target, 60.Seconds());
        await waitForFullSlotOwnershipAsync(60.Seconds());

        // Keep the stream running across the handoff so the new owner has real work waiting for it.
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await pump;

        var everything = published.ToArray();
        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(everything, 120.Seconds()))
            .ShouldBeTrue("Not every published letter was handled after the live handoff");

        _output.WriteLine(
            $"{everything.Length} published, {NativeAckPartitionedProcessing.Ledger.Handled.Count} handled");

        NativeAckPartitionedProcessing.AssertNoIntraGroupConcurrency();
        NativeAckPartitionedProcessing.AssertEveryLetterWasHandled(everything);
        NativeAckPartitionedProcessing.AssertGroupsNeverStraddleSlots();

        // The moved slot has to have been worked by both nodes, or the handoff window was never entered.
        var workersOnMovedSlot = NativeAckPartitionedProcessing.Ledger.Handled
            .Where(x => x.Destination!.Segments.Last() == movedSlot.Segments.Last())
            .Select(x => x.NodeName)
            .Distinct()
            .ToArray();

        workersOnMovedSlot.ShouldContain(originalOwnerName);
        workersOnMovedSlot.Length.ShouldBeGreaterThan(1,
            $"Slot {movedSlot} was only ever processed by {workersOnMovedSlot.Join(", ")}, so no handoff happened");
    }

    private async Task waitForSlotOwnerAsync(Uri slotAgent, IHost expected, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (slotAgentsOn(expected).Contains(slotAgent)) return;
            await Task.Delay(250.Milliseconds());
        }

        throw new TimeoutException(
            $"{slotAgent} never moved to {expected.GetRuntime().Options.ServiceName}");
    }
}
