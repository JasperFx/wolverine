using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace PostgresqlTests.Transport;

/// <summary>
/// Global partitioning over sharded PostgreSQL queues in a REAL Balanced cluster: several hosts in one process
/// sharing one message store, with the leader moving slots around as nodes join, leave and crash.
///
/// <para>
/// Every one of GH-4673, GH-4700, GH-4776, GH-4777 and GH-4822 was reported from exactly this shape -- two or
/// three nodes in one process, Balanced mode, default settings -- and every one of their regression fixtures is
/// a Solo host that fakes a non-owner by stopping a listener. That proves the mechanism each fix added. It does
/// not prove the cluster: the leader's reassignment protocol, the ex-owner's agent still being registered, the
/// durability agent living on a different node than the slot, the orphan sweep after a crash. This fixture
/// stands the cluster up and asks the one question global partitioning promises to answer: no two messages of
/// a group id ever run at the same time, and every message runs.
/// </para>
///
/// <para>
/// Both ways onto a slot are loaded on every test: the local shortcut (published by the owner) and the shard
/// queue table (sent by a non-owner, or straight to the slot endpoint), because their inbox rows carry different
/// addresses and are released by different code.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class global_partition_slots_move_in_a_balanced_cluster : IAsyncLifetime
{
    private const string Schema = "gpcluster";
    private const string BaseName = "cluster";
    private const int SlotCount = 4;

    private readonly List<IHost> _hosts = new();

    public async ValueTask InitializeAsync()
    {
        ClusterStepHandler.Reset();

        // Nothing is running yet, so dropping both schemas is the cheapest way to a known-empty cluster: no
        // node rows from a crashed earlier run, no dormant inbox rows for a recovery loop to claim, no queue
        // backlog. Every host re-provisions on start. AutoPurgeOnStartup is deliberately NOT used below --
        // a joining node would purge the queue tables out from under the running cluster.
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        var drop = conn.CreateCommand();
        drop.CommandText = $"drop schema if exists {Schema}_queues cascade; drop schema if exists {Schema} cascade;";
        await drop.ExecuteNonQueryAsync();
        await conn.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.GetRuntime().Agents.DisableHealthChecks();
        }

        foreach (var host in _hosts.ToArray())
        {
            try
            {
                await host.StopAsync();
            }
            catch (Exception)
            {
                // A host that was "crashed" is already half torn down
            }

            host.Dispose();
        }

        _hosts.Clear();
        ClusterStepHandler.Reset();
    }

    // ---- cluster lifecycle -------------------------------------------------------------------------

    private async Task<IHost> startNodeAsync()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;

                // Tight so the cluster reacts within seconds rather than the production defaults of 10-30s
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.FirstHealthCheckExecution = 1.Seconds();
                opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
                opts.Durability.StaleNodeTimeout = 3.Seconds();
                opts.Durability.OrphanedMessageSweepPollingTime = 1.Seconds();
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, Schema,
                        transportSchema: $"{Schema}_queues")
                    .AutoProvision();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ClusterStepHandler));

                opts.MessagePartitioning.ByMessage<ClusterStep>(x => x.GroupId.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues(BaseName, SlotCount);
                    topology.Message<ClusterStep>();
                });
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    /// <summary>Graceful shutdown: the drain half of a rolling deploy.</summary>
    private async Task stopNodeAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
        _hosts.Remove(host);
    }

    /// <summary>
    /// A crash stand-in for a node with no broker to cut it off from. <see cref="StopMode.Quick"/> skips the
    /// listener drain, the ownership release and the agent teardown -- so nothing is released, the node row is
    /// left behind with a heartbeat that stops, and the leader has to notice on its own. Disposal then kills the
    /// listeners without a drain. Not a process kill, but the same thing from the cluster's point of view.
    /// </summary>
    private async Task crashNodeAsync(IHost host)
    {
        var runtime = host.GetRuntime();
        runtime.Agents.DisableHealthChecks();
        runtime.StopMode = StopMode.Quick;
        await host.StopAsync();
        host.Dispose();
        _hosts.Remove(host);
    }

    private static int nodeNumber(IHost host) => host.GetRuntime().DurabilitySettings.AssignedNodeNumber;

    private static PostgresqlQueue[] slotsOf(IHost host)
    {
        var transport = host.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>();
        return Enumerable.Range(1, SlotCount).Select(i => transport.Queues[$"{BaseName}{i}"]).ToArray();
    }

    /// <summary>The slots this host's listening agents are accepting on -- the fact GlobalPartitionedRoute reads.</summary>
    private static Uri[] slotsOwnedBy(IHost host)
    {
        return slotsOf(host)
            .Where(x => host.GetRuntime().Endpoints.FindListeningAgent(x.Uri) is { Status: ListeningStatus.Accepting })
            .Select(x => x.Uri)
            .ToArray();
    }

    private IHost? ownerOf(Uri slot)
    {
        return _hosts.SingleOrDefault(h => slotsOwnedBy(h).Contains(slot));
    }

    private bool everySlotOwnedExactlyOnce()
    {
        var slots = slotsOf(_hosts[0]).Select(x => x.Uri);
        return slots.All(slot => _hosts.Count(h => slotsOwnedBy(h).Contains(slot)) == 1);
    }

    private string describeOwnership()
    {
        return _hosts.Select(h => $"node {nodeNumber(h)}=[{slotsOwnedBy(h).Select(x => x.ToString()).Join(", ")}]").Join("; ");
    }

    /// <summary>
    /// Wait until every slot has exactly one owner and that holds across an assignment cycle. "Settled" alone
    /// is not enough right after a membership change: a lone node holding every slot IS settled, and the
    /// leader's rebalance may not have started yet. So a change-shaped wait also names what the rebalance has
    /// to have produced -- the joining node owning something, the departed node's slots re-homed -- and
    /// only counts a state as settled once that is true too.
    /// </summary>
    private async Task waitForSettledOwnershipAsync(TimeSpan timeout, Func<bool>? rebalanceHasLanded = null)
    {
        bool settled() => everySlotOwnedExactlyOnce() && (rebalanceHasLanded?.Invoke() ?? true);

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (settled())
            {
                // Has to hold across an assignment cycle to count as settled rather than mid-move
                await Task.Delay(1500.Milliseconds());
                if (settled()) return;
            }

            await Task.Delay(200.Milliseconds());
        }

        throw new TimeoutException($"The {SlotCount} slots never settled one-per-node with the expected rebalance. Saw: {describeOwnership()}");
    }

    // ---- load ----------------------------------------------------------------------------------------

    /// <summary>
    /// A group id that GlobalPartitionedRoute on <paramref name="host"/> sends to the given slot. Asked of the
    /// router so the test cannot drift from the hashing; whether it lands on the companion or the shard queue
    /// depends on whether the host owns the slot at the time.
    /// </summary>
    private static Guid groupIdFor(IHost host, Uri slot)
    {
        var bus = host.MessageBus();
        var endpoint = host.GetRuntime().Endpoints.EndpointFor(slot)!;
        var companion = endpoint.GlobalPartitionLocalQueueUri!;

        for (var i = 0; i < 1000; i++)
        {
            var candidate = Guid.NewGuid();
            var destination = bus.PreviewSubscriptions(new ClusterStep(candidate, 0)).Single().Destination;
            if (destination == slot || destination == companion) return candidate;
        }

        throw new TimeoutException($"Could not find a group id routing to {slot}");
    }

    /// <summary>
    /// Load one slot both ways: a group published by its owner (the local shortcut, rows at the companion
    /// address) and a group sent straight to the slot endpoint (the shard queue table, rows at the slot address).
    /// Returns the (group, numbers) pairs that have to be handled.
    /// </summary>
    private async Task<List<(Guid GroupId, int[] Numbers)>> loadSlotBothWaysAsync(IHost owner, Uri slot, int perGroup)
    {
        var shortcut = groupIdFor(owner, slot);
        var throughTheTable = Guid.NewGuid();
        var numbers = Enumerable.Range(1, perGroup).ToArray();

        var bus = owner.MessageBus();
        foreach (var n in numbers)
        {
            await bus.PublishAsync(new ClusterStep(shortcut, n));
            await bus.EndpointFor(slot).SendAsync(new ClusterStep(throughTheTable, n));
        }

        return [(shortcut, numbers), (throughTheTable, numbers)];
    }

    private async Task waitForAllHandledAsync(IEnumerable<(Guid GroupId, int[] Numbers)> expected, TimeSpan timeout)
    {
        var all = expected.SelectMany(x => x.Numbers.Select(n => (x.GroupId, n))).ToArray();

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (all.All(ClusterStepHandler.HasHandled)) return;
            await Task.Delay(200.Milliseconds());
        }

        var missing = all.Where(x => !ClusterStepHandler.HasHandled(x)).ToArray();
        throw new TimeoutException(
            $"{missing.Length} of {all.Length} messages were never handled: " +
            $"[{missing.Select(x => $"{x.GroupId}/{x.n}").Join(", ")}]. Ownership: {describeOwnership()}. " +
            $"Inbox: {await describeInboxAsync()}");
    }

    private async Task<string> describeInboxAsync()
    {
        var rows = await _hosts[0].GetRuntime().Storage.Admin.AllIncomingAsync();
        return rows.Where(x => x.Status != EnvelopeStatus.Handled)
            .Select(x => $"{x.Id}@{x.Destination} {x.Status} owner={x.OwnerId}").Join("; ");
    }

    /// <summary>
    /// After everything has run, nothing may be left Incoming: not at owner 0 (a row nobody picked up) and
    /// above all not owned by a live node (a row nobody CAN pick up).
    /// </summary>
    private async Task assertNothingStrandedAsync()
    {
        var live = _hosts.Select(nodeNumber).ToHashSet();

        await waitForAsync(async () =>
            {
                var rows = await _hosts[0].GetRuntime().Storage.Admin.AllIncomingAsync();
                return rows.All(x => x.Status != EnvelopeStatus.Incoming);
            },
            async () =>
            {
                var rows = await _hosts[0].GetRuntime().Storage.Admin.AllIncomingAsync();
                var stuck = rows.Where(x => x.Status == EnvelopeStatus.Incoming).ToArray();
                var ownedByLive = stuck.Where(x => live.Contains(x.OwnerId)).ToArray();
                return $"{stuck.Length} rows still Incoming, {ownedByLive.Length} of them owned by a LIVE node " +
                       $"(invisible to every recovery sweep): {stuck.Select(x => $"{x.Id}@{x.Destination} owner={x.OwnerId}").Join("; ")}";
            }, 15.Seconds());
    }

    // ---- the tests -----------------------------------------------------------------------------------

    /// <summary>
    /// GH-4777 as reported: a node joins, the leader hands it a slot, and the previous owner's backlog on that
    /// slot -- on both paths -- must be finished or handed over without ever overlapping the new owner.
    /// </summary>
    [Fact]
    public async Task a_joining_node_takes_slots_without_overlapping_the_backlog_on_either_path()
    {
        var nodeA = await startNodeAsync();
        await waitForSettledOwnershipAsync(30.Seconds());
        slotsOwnedBy(nodeA).Length.ShouldBe(SlotCount, "A lone node owns every slot");

        // A backlog on every slot, both ways in. Slow enough to outlast the join + reassignment.
        ClusterStepHandler.Delay = 250.Milliseconds();
        var expected = new List<(Guid, int[])>();
        foreach (var slot in slotsOf(nodeA))
        {
            expected.AddRange(await loadSlotBothWaysAsync(nodeA, slot.Uri, 20));
        }

        await waitForAsync(() => ClusterStepHandler.Handled.Count >= 2, () => Task.FromResult("The backlog never started"));

        var nodeB = await startNodeAsync();
        await waitForSettledOwnershipAsync(45.Seconds(), () => slotsOwnedBy(nodeB).Length > 0);

        var moved = slotsOwnedBy(nodeB);
        moved.Length.ShouldBeGreaterThan(0, $"The leader never gave the joining node a slot. {describeOwnership()}");

        var handledAtHandoff = ClusterStepHandler.Handled.Count;
        handledAtHandoff.ShouldBeLessThan(expected.Sum(x => x.Item2.Length),
            "Precondition: the backlog has to still exist when the slot moves, or the handoff is never exercised. " +
            "Raise the per-group count or the delay if this trips.");

        // New traffic on the moved slots from their new owner, while the old owner may still be finishing
        var busB = nodeB.MessageBus();
        foreach (var slot in moved)
        {
            var groupId = groupIdFor(nodeB, slot);
            var numbers = Enumerable.Range(1, 3).ToArray();
            foreach (var n in numbers) await busB.PublishAsync(new ClusterStep(groupId, n));
            expected.Add((groupId, numbers));
        }

        await waitForAllHandledAsync(expected, 90.Seconds());

        ClusterStepHandler.Violations.ShouldBeEmpty(
            "Two messages of one group id ran at the same time across the handoff: " + ClusterStepHandler.Violations.Join("; "));

        await assertNothingStrandedAsync();

        // Every message handled after the move ran on the slot's current owner, never on the ex-owner. The
        // leader waits for the ex-owner's CONFIRMED stop -- which includes the companion drain -- before it
        // starts the slot on the new owner, so nothing the ex-owner runs may finish after the new owner begins.
        foreach (var slot in moved)
        {
            var slotName = ClusterStepHandler.SlotNameOf(slot);
            var handoff = ClusterStepHandler.HandoffAt(nodeNumber(nodeB), slotName);
            handoff.ShouldNotBe(DateTimeOffset.MaxValue, $"The new owner never handled anything for {slot}");

            var late = ClusterStepHandler.Received
                .Where(x => ClusterStepHandler.SlotNameOf(x.Destination!) == slotName && x.HandledAt > handoff && x.Node != nodeNumber(nodeB))
                .ToArray();

            late.ShouldBeEmpty($"The ex-owner kept executing {slot} after the new owner had started on it: " +
                               late.Select(x => $"{x.GroupId}/{x.Number} on node {x.Node} at {x.HandledAt:HH:mm:ss.fff} (handoff {handoff:HH:mm:ss.fff})").Join("; "));
        }
    }

    /// <summary>
    /// GH-4776's second report: a slot owner stops gracefully with a backlog, and the survivors -- one of which
    /// runs the durability agent -- pick it up through the slot's new owner, not through whoever holds the agent.
    /// </summary>
    [Fact]
    public async Task a_node_leaving_gracefully_hands_its_backlog_to_the_slots_next_owner()
    {
        var nodeA = await startNodeAsync();
        await waitForSettledOwnershipAsync(30.Seconds());
        var nodeB = await startNodeAsync();
        var nodeC = await startNodeAsync();
        await waitForSettledOwnershipAsync(45.Seconds(),
            () => slotsOwnedBy(nodeB).Length > 0 || slotsOwnedBy(nodeC).Length > 0);

        // The node to take away: owns at least one slot, is not the leader (so only the slots move)
        var leaving = new[] { nodeB, nodeC }.First(h => slotsOwnedBy(h).Length > 0);
        var leavingSlots = slotsOwnedBy(leaving);

        ClusterStepHandler.Delay = 250.Milliseconds();
        var expected = new List<(Guid, int[])>();
        foreach (var slot in leavingSlots)
        {
            expected.AddRange(await loadSlotBothWaysAsync(leaving, slot, 12));
        }

        await waitForAsync(() => ClusterStepHandler.Handled.Count >= 2, () => Task.FromResult("The backlog never started"));

        await stopNodeAsync(leaving);

        await waitForSettledOwnershipAsync(45.Seconds(), () => leavingSlots.All(slot => ownerOf(slot) != null));
        foreach (var slot in leavingSlots)
        {
            ownerOf(slot).ShouldNotBeNull($"Slot {slot} was never reassigned after its owner left. {describeOwnership()}");
        }

        await waitForAllHandledAsync(expected, 90.Seconds());

        ClusterStepHandler.Violations.ShouldBeEmpty(
            "Two messages of one group id ran at the same time across the departure: " + ClusterStepHandler.Violations.Join("; "));

        await assertNothingStrandedAsync();
    }

    /// <summary>
    /// The crash. Nothing is released and the node row stays behind, so the whole recovery chain has to fire:
    /// stale node ejection by the leader, slot reassignment, the orphan sweep releasing the dead node's rows,
    /// and the new owner's recovery loops picking them up -- for both paths.
    /// </summary>
    [Fact]
    public async Task a_crashed_nodes_slots_and_backlog_are_recovered_by_the_survivors()
    {
        var nodeA = await startNodeAsync();
        await waitForSettledOwnershipAsync(30.Seconds());
        var nodeB = await startNodeAsync();
        await waitForSettledOwnershipAsync(45.Seconds(), () => slotsOwnedBy(nodeB).Length > 0);

        var crashing = nodeB;
        var crashingSlots = slotsOwnedBy(crashing);
        crashingSlots.Length.ShouldBeGreaterThan(0, $"The second node never got a slot. {describeOwnership()}");

        ClusterStepHandler.Delay = 250.Milliseconds();
        var expected = new List<(Guid, int[])>();
        foreach (var slot in crashingSlots)
        {
            expected.AddRange(await loadSlotBothWaysAsync(crashing, slot, 12));
        }

        await waitForAsync(() => ClusterStepHandler.Handled.Count >= 2, () => Task.FromResult("The backlog never started"));

        var crashedNumber = nodeNumber(crashing);
        await crashNodeAsync(crashing);

        // Preconditions that make this a crash and not a graceful stop wearing a different name: the node row
        // is still registered and its backlog is still owned by it. A graceful stop would have deleted the row
        // and drained the companion queues, and then every assertion below would pass for the wrong reason.
        var nodes = nodeA.GetRuntime().Storage.Nodes;
        (await nodes.LoadAllNodesAsync(TestContext.Current.CancellationToken))
            .ShouldContain(x => x.AssignedNodeNumber == crashedNumber,
                "Precondition: the crashed node must still be registered -- nothing deregistered it");

        (await nodeA.GetRuntime().Storage.Admin.AllIncomingAsync())
            .Where(x => x.Status == EnvelopeStatus.Incoming && x.OwnerId == crashedNumber)
            .ShouldNotBeEmpty("Precondition: the crashed node must still own its backlog -- nothing released it");

        // The leader has to notice on its own: a sustained missing heartbeat, then the ejection that deletes the
        // row and releases everything the dead node owned in one statement.
        await waitForAsync(async () => !(await nodes.LoadAllNodesAsync(TestContext.Current.CancellationToken))
                .Any(x => x.AssignedNodeNumber == crashedNumber),
            () => Task.FromResult($"The leader never ejected the crashed node. {describeOwnership()}"), 45.Seconds());

        await waitForSettledOwnershipAsync(60.Seconds(), () => crashingSlots.All(slot => ownerOf(slot) == nodeA));
        foreach (var slot in crashingSlots)
        {
            ownerOf(slot).ShouldBe(nodeA, $"Slot {slot} was never reassigned after its owner crashed. {describeOwnership()}");
        }

        await waitForAllHandledAsync(expected, 120.Seconds());

        // A crash is the one case where an in-flight handler on the dead node can legitimately overlap the
        // survivor -- the dead node cannot be told to stop. The ejection takes seconds and a handler takes
        // milliseconds, so in practice there is none; report rather than assert.
        if (ClusterStepHandler.Violations.Any())
        {
            Console.WriteLine("Overlap during the crash window: " + ClusterStepHandler.Violations.Join("; "));
        }

        await assertNothingStrandedAsync();
    }

    private static async Task waitForAsync(Func<bool> condition, Func<Task<string>> diagnostic)
    {
        await waitForAsync(() => Task.FromResult(condition()), diagnostic, 30.Seconds());
    }

    private static async Task waitForAsync(Func<Task<bool>> condition, Func<Task<string>> diagnostic, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200.Milliseconds());
        }

        throw new TimeoutException(await diagnostic());
    }
}

public record ClusterStep(Guid GroupId, int Number);

public static class ClusterStepHandler
{
    public static readonly ConcurrentBag<(Guid GroupId, int Number, int Node, Uri? Destination, DateTimeOffset HandledAt)>
        Received = new();

    public static readonly ConcurrentBag<(Guid GroupId, int Number)> Handled = new();

    /// <summary>Group ids observed executing concurrently -- across ALL nodes, since every host shares this process.</summary>
    public static readonly ConcurrentBag<string> Violations = new();

    private static readonly ConcurrentDictionary<Guid, (int Number, int Node)> _running = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _firstSeenOn = new();

    public static TimeSpan Delay = 250.Milliseconds();

    public static void Reset()
    {
        Received.Clear();
        Handled.Clear();
        Violations.Clear();
        _running.Clear();
        _firstSeenOn.Clear();
        Delay = 250.Milliseconds();
    }

    public static bool HasHandled((Guid GroupId, int Number) key) => Handled.Contains(key);

    /// <summary>When the given node first STARTED handling anything for the given slot name -- its effective takeover.</summary>
    public static DateTimeOffset HandoffAt(int node, string slotName)
    {
        return _firstSeenOn.TryGetValue($"{node}:{slotName}", out var at) ? at : DateTimeOffset.MaxValue;
    }

    /// <summary>
    /// The slot a destination belongs to, whichever way in: <c>postgresql://cluster1/</c> and its companion
    /// <c>local://global-cluster1/</c> are both "cluster1". The name is the URI's HOST, not a path segment.
    /// </summary>
    public static string SlotNameOf(Uri destination)
    {
        var host = destination.Host;
        return host.StartsWith("global-") ? host["global-".Length..] : host;
    }

    public static async Task Handle(ClusterStep step, Envelope envelope, IWolverineRuntime runtime)
    {
        var node = runtime.DurabilitySettings.AssignedNodeNumber;

        if (!_running.TryAdd(step.GroupId, (step.Number, node)))
        {
            var other = _running[step.GroupId];
            Violations.Add($"{step.GroupId}/{step.Number} on node {node} started while /{other.Number} was running on node {other.Node}");
        }

        try
        {
            var slotName = envelope.Destination == null ? "" : SlotNameOf(envelope.Destination);
            _firstSeenOn.TryAdd($"{node}:{slotName}", DateTimeOffset.UtcNow);

            await Task.Delay(Delay);

            Received.Add((step.GroupId, step.Number, node, envelope.Destination, DateTimeOffset.UtcNow));
            Handled.Add((step.GroupId, step.Number));
        }
        finally
        {
            _running.TryRemove(step.GroupId, out _);
        }
    }
}
