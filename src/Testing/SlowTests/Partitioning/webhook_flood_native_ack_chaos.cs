using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RabbitMQ.Client;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests.Partitioning;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace SlowTests.Partitioning;

/// <summary>
/// GH-3713. The capstone reproduction for the native-ack partitioning wave (#3706 -> #3708 -> #3709 -> #3710):
/// the reporting client's actual shape -- a sustained webhook flood, grouped by an entity id, across a
/// <b>five node</b> cluster -- put under chaos, with the intra-group concurrency invariant asserted
/// cluster-wide throughout and the duplicate-execution rate measured rather than assumed.
/// </summary>
/// <remarks>
/// <para><b>The guarantee under test, stated exactly:</b> no two messages sharing a group id execute
/// concurrently. Within a node the sequential lane inside the slot's own receiver enforces it; across the
/// cluster the exclusive slot listener enforces it, because exactly one node consumes a given slot.</para>
///
/// <para>Ordering is per-slot <b>best effort</b>, not per-group guaranteed. Redelivery and requeue may
/// reorder, and the ordering unit is the slot rather than the group, so two entities hashing to the same slot
/// serialize against each other. Nothing here asserts ordering, and an assertion that presumed it would fail
/// for reasons that are not bugs.</para>
///
/// <para><b>Why there is a database here at all, and what it is emphatically not doing.</b> The message path
/// is storage-free: every slot is <see cref="EndpointMode.NativeAck" />, so no envelope is ever written to an
/// inbox, no outbox is involved, and no webhook event touches Postgres at any point.
/// <see cref="no_webhook_event_ever_touches_the_database" /> asserts that against the incoming table rather
/// than claiming it. Postgres is present purely as the cluster's <i>node registry</i>, because dynamic
/// one-consumer-per-slot assignment runs through <c>NodeAgentController</c>, which
/// <c>WolverineRuntime.startAgentsAsync</c> skips entirely when the store is a <c>NullMessageStore</c> -- so a
/// genuinely storeless cluster has no <c>ExclusiveListenerFamily</c>, falls back to Solo, and starts every
/// listener on every node. That limitation is GH-4072, and it is the reason this suite coordinates through a
/// store while still keeping the message path free of one. Nothing here should be read as contradicting the
/// storage-free claim; the storage-free single-node and static-ownership shapes are covered by
/// <c>native_ack_global_partitioning_cluster</c> in the RabbitMQ suite.</para>
///
/// <para><b>Gating.</b> This suite is wall-clock bound by construction -- it waits out health checks, stale
/// node detection and slot reassignment five times over -- and it lives in <c>SlowTests</c>, which runs only
/// from the manual <c>slow-tests.yml</c> workflow. It is deliberately not on the pull request path. The
/// default flood is a few thousand events; set <c>WOLVERINE_WEBHOOK_FLOOD_SIZE</c> to run the client's real
/// 50k scale.</para>
/// </remarks>
[Collection("webhook_flood")]
public class webhook_flood_native_ack_chaos : IAsyncLifetime
{
    private const string SchemaName = "webhook_flood";
    private const string BaseName = "webhookflood";
    private const int SlotCount = 5;
    private const int NodeCount = 5;
    private const int EntityCount = 500;

    /// <summary>
    /// How deep the broker's ready backlog has to get before chaos counts as landing "mid-flood". Comfortably
    /// more than the whole cluster's total prefetch window (5 slots x 36), so every consuming node is holding
    /// a full unacked window at the moment it is disturbed.
    /// </summary>
    private const int SaturationDepth = 400;

    /// <summary>
    /// <c>ExclusiveListenerFamily.SchemeName</c>. Repeated rather than referenced because that type is
    /// internal to Wolverine and this assembly has no internals access.
    /// </summary>
    private const string ListenerAgentScheme = "wolverine-listener";

    private readonly List<IHost> _hosts = [];
    private readonly ITestOutputHelper _output;
    private readonly RabbitBrokerProbe _probe = new(BaseName, SlotCount);
    private int _generation;
    private int _peakBacklog;
    private int _unackedAtDisruption;

    public webhook_flood_native_ack_chaos(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The client's report was a flood, so the default is a real one -- but a five node cluster plus slot
    /// reassignment is already minutes of wall clock, so the 50k scale is opt-in rather than the default.
    /// </summary>
    private static int floodSize =>
        int.TryParse(Environment.GetEnvironmentVariable("WOLVERINE_WEBHOOK_FLOOD_SIZE"), out var size)
            ? size
            : 4000;

    public async ValueTask InitializeAsync()
    {
        NativeAckPartitionedProcessing.Ledger.Clear();
        _peakBacklog = 0;
        _unackedAtDisruption = 0;

        // Wide enough that a handler is genuinely still in flight when its node is taken away -- the window a
        // duplicate execution has to land in is exactly this wide, so a dwell near zero measures zero
        // duplicates and proves nothing. It also caps the cluster's throughput at
        // slots x lanes / dwell = 5 x 5 / 250ms = 100 events/sec, which is what lets an unpaced burst of a
        // few thousand events keep the queues genuinely saturated for the whole chaos window.
        NativeAckPartitionedProcessing.Dwell = 250.Milliseconds();

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
        await conn.CloseAsync();

        // Stateful broker discipline: residue from an earlier run would be counted as this run's duplicates
        // and would silently corrupt the one number this suite exists to produce.
        await deleteSlotQueuesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _hosts.Reverse();
        foreach (var host in _hosts.ToArray())
        {
            try
            {
                await stopHostAsync(host);
            }
            catch (Exception)
            {
                // Nothing useful to do about a host that will not shut down cleanly during teardown
            }
        }

        _hosts.Clear();
        _probe.Dispose();
        NativeAckPartitionedProcessing.Ledger.Clear();
        NativeAckPartitionedProcessing.Dwell = 50.Milliseconds();
    }

    private static async Task deleteSlotQueuesAsync()
    {
        await using var connection = await new ConnectionFactory { HostName = "localhost", Port = 5672 }
            .CreateConnectionAsync();

        for (var i = 1; i <= SlotCount; i++)
        {
            // A channel per queue: deleting a queue that does not exist yet kills the channel it was tried on.
            try
            {
                await using var channel = await connection.CreateChannelAsync();
                await channel.QueueDeleteAsync($"{BaseName}{i}", false, false);
            }
            catch (Exception)
            {
                // First run on a clean broker -- nothing to delete
            }
        }
    }

    // ---- cluster lifecycle -------------------------------------------------------------------------

    private async Task<IHost> startHostAsync(string nodeName)
    {
        var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Balanced;
            opts.Durability.HealthCheckPollingTime = 1.Seconds();
            opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
            opts.Durability.CheckAssignmentPeriod = 1.Seconds();
            opts.Durability.StaleNodeTimeout = 3.Seconds();

            // Named per node so the broker can be asked to drop exactly this host's connections -- see
            // RabbitBrokerProbe.ForceCloseConnectionsAsync, which is what makes killHostAsync a real kill.
            opts.UseRabbitMq(factory =>
                {
                    factory.HostName = "localhost";
                    factory.Port = 5672;
                    factory.ClientProvidedName = nodeName;
                })
                .EnableWolverineControlQueues()
                .AutoProvision();

            // Node/agent coordination only. No webhook event is ever written here -- asserted by
            // no_webhook_event_ever_touches_the_database.
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

    /// <summary>Graceful shutdown -- the drain half of a drain-and-replace rolling deploy.</summary>
    private async Task stopHostAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
        _hosts.Remove(host);
    }

    /// <summary>
    /// A genuine hard kill: the broker drops this node's connections out from under it, and only then is the
    /// host torn down.
    /// </summary>
    /// <remarks>
    /// <para>The obvious implementation -- just <c>host.Dispose()</c> -- is <b>not</b> a kill, and an earlier
    /// version of this suite made exactly that mistake. <c>WolverineRuntime</c>'s <c>DisposeAsync</c> calls
    /// <c>StopAsync</c> when the runtime has not already stopped, so disposal takes the full graceful path and
    /// the listeners drain: in-flight handlers finish and their acknowledgements go out. The suite duly
    /// measured zero duplicate executions on a "hard kill" -- a true number about a graceful shutdown, and a
    /// misleading one about a crash.</para>
    ///
    /// <para>Closing the connections broker-side first is what makes the difference. Everything unacknowledged
    /// on them is requeued at once, and the handlers that were mid-flight complete into a channel that no
    /// longer exists -- their work happened, their acknowledgement did not. That is the only way a duplicate
    /// execution is produced, so it is the only honest way to measure how many.</para>
    /// </remarks>
    private async Task killHostAsync(IHost host)
    {
        var nodeName = host.GetRuntime().Options.ServiceName;

        var closed = await _probe.ForceCloseConnectionsAsync(nodeName);
        _output.WriteLine($"  broker force-closed {closed} connection(s) belonging to {nodeName}");

        closed.ShouldBeGreaterThan(0,
            $"No broker connection was force-closed for {nodeName}, so this was not a hard kill and any "
            + "duplicate measurement taken from it would describe a graceful shutdown instead.");

        _hosts.Remove(host);
        host.Dispose();
    }

    private static Uri slotAgentUri(int slotNumber) =>
        new($"{ListenerAgentScheme}://rabbitmq/{BaseName}{slotNumber}");

    private IReadOnlyList<Uri> slotAgentsOn(IHost host)
    {
        var running = host.RunningAgents().ToHashSet();
        return Enumerable.Range(1, SlotCount).Select(slotAgentUri).Where(running.Contains).ToArray();
    }

    /// <summary>Every slot consumed by exactly one node -- never zero, never two.</summary>
    private bool everySlotOwnedExactlyOnce()
    {
        return Enumerable.Range(1, SlotCount)
            .All(slot => _hosts.Count(h => slotAgentsOn(h).Contains(slotAgentUri(slot))) == 1);
    }

    private async Task waitForFullSlotOwnershipAsync(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (everySlotOwnedExactlyOnce())
            {
                // Has to hold across a health check cycle to count as settled rather than mid-flap.
                await Task.Delay(1500.Milliseconds());
                if (everySlotOwnedExactlyOnce()) return;
            }

            await Task.Delay(250.Milliseconds());
        }

        var report = _hosts.Select(h =>
            $"{h.GetRuntime().Options.ServiceName}=[{slotAgentsOn(h).Select(x => x.ToString()).Join(", ")}]").Join("; ");

        throw new TimeoutException($"The {SlotCount} slot listeners never settled one-per-node. Saw: {report}");
    }

    private async Task<IHost> startClusterAsync(string prefix)
    {
        var leader = await startHostAsync($"{prefix}1");
        for (var i = 2; i <= NodeCount; i++)
        {
            await startHostAsync($"{prefix}{i}");
        }

        (await leader.WaitUntilAssumesLeadershipAsync(60.Seconds()))
            .ShouldBeTrue("The first host never assumed leadership");

        assertSlotsAreNativeAck(leader);
        await waitForFullSlotOwnershipAsync(120.Seconds());

        return leader;
    }

    private void assertSlotsAreNativeAck(IHost host)
    {
        var runtime = host.GetRuntime();

        for (var i = 1; i <= SlotCount; i++)
        {
            var endpoint = runtime.Endpoints.EndpointFor(new Uri($"rabbitmq://queue/{BaseName}{i}"))
                .ShouldNotBeNull($"No endpoint was built for slot {BaseName}{i}");

            endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
            endpoint.ListenerScope.ShouldBe(ListenerScope.Exclusive);
        }
    }

    /// <summary>
    /// The prefetch window per slot, read off the endpoint rather than recomputed here -- the documentation's
    /// claimed duplicate bound is stated in terms of this number, so the comparison has to use the real one.
    /// </summary>
    private int prefetchPerSlot(IHost host)
    {
        var queue = (RabbitMqQueue)host.GetRuntime().Endpoints
            .EndpointFor(new Uri($"rabbitmq://queue/{BaseName}1"))!;

        return queue.PreFetchCount;
    }

    // ---- the flood ---------------------------------------------------------------------------------

    /// <summary>
    /// A flood. <see cref="TimeSpan.Zero" /> -- the default -- publishes unpaced, which is what actually
    /// saturates the cluster; see <see cref="WebhookFloodDriver" /> for why pacing produced a vacuous
    /// measurement.
    /// </summary>
    private WebhookFloodDriver buildFlood(int sizeMultiplier = 1, TimeSpan duration = default)
    {
        var stream = WebhookFloodDriver.BuildSkewedEntityStream(EntityCount, floodSize * sizeMultiplier,
            seed: 3713);

        return new WebhookFloodDriver(stream, () => _hosts.ToArray(), duration);
    }

    /// <summary>
    /// Block until the broker is genuinely backed up, so that chaos introduced afterwards lands on a
    /// saturated cluster with a full unacked window rather than on one that is keeping up.
    /// </summary>
    /// <remarks>
    /// This gate replaced an earlier one that waited on a handled-event count. That version passed happily
    /// against a cluster whose queues were empty the whole time, and the suite duly measured a duplicate rate
    /// of zero in every phase -- a vacuous result. Saturation is the precondition for the measurement meaning
    /// anything, so it is asserted here rather than assumed.
    /// </remarks>
    private async Task<RabbitBrokerProbe.BrokerReading> waitForSaturatedBacklogAsync(int depth, TimeSpan timeout)
    {
        var reading = await _probe.WaitForBacklogAsync(depth, timeout);

        if (reading is null)
        {
            var latest = await _probe.ReadAsync();
            throw new TimeoutException(
                $"The broker backlog never reached {depth} within {timeout.TotalSeconds:N0}s "
                + $"(latest reading: {(latest is null ? "unreachable" : $"{latest.Ready} ready, {latest.Unacknowledged} unacked")}). "
                + "Without a saturated cluster the duplicate measurement is vacuous.");
        }

        recordBrokerReading(reading);

        return reading;
    }

    private void recordBrokerReading(RabbitBrokerProbe.BrokerReading reading)
    {
        _peakBacklog = Math.Max(_peakBacklog, reading.Ready);
    }

    /// <summary>
    /// Snapshot the broker's unacked window at the instant chaos is introduced. That number is the population
    /// that gets requeued, and it is the quantity the documentation's "bounded by prefetch depth" claim is
    /// about -- so it is measured rather than inferred from configuration.
    /// </summary>
    private async Task captureUnackedAtDisruptionAsync()
    {
        var reading = await _probe.ReadAsync();
        if (reading is null) return;

        recordBrokerReading(reading);
        _unackedAtDisruption = Math.Max(_unackedAtDisruption, reading.Unacknowledged);

        _output.WriteLine(
            $"  broker at disruption: {reading.Ready} ready, {reading.Unacknowledged} unacknowledged");
    }

    /// <summary>
    /// The assertions every phase makes, in the issue's priority order. Concurrency first and hard, then
    /// completeness, then routing. Ordering is deliberately absent.
    /// </summary>
    private DuplicateExecutionReport settleAndAssert(string phase, WebhookFloodDriver flood, IHost witness,
        int disruptedNodes)
    {
        var published = flood.Published;

        NativeAckPartitionedProcessing.AssertNoIntraGroupConcurrency();
        NativeAckPartitionedProcessing.AssertEveryLetterWasHandled(published);
        NativeAckPartitionedProcessing.AssertGroupsNeverStraddleSlots();
        NativeAckPartitionedProcessing.AssertEverySlotWasUsed(SlotCount);

        var report = DuplicateExecutionReport.From(phase, published, prefetchPerSlot(witness), SlotCount,
            disruptedNodes, _peakBacklog, _unackedAtDisruption);

        _output.WriteLine(report.Describe());
        _output.WriteLine($"  rejected sends (not published, not lost): {flood.RejectedSends}");
        _output.WriteLine(
            $"  nodes that did work: {NativeAckPartitionedProcessing.Ledger.Handled.Select(x => x.NodeName).Distinct().OrderBy(x => x).Join(", ")}");

        return report;
    }

    // ---- phase 1: steady state ---------------------------------------------------------------------

    /// <summary>
    /// The control run. Five nodes, a sustained flood, no chaos at all. Whatever duplicate rate this
    /// produces is the floor -- it is the cost of the mode itself rather than the cost of a disruption.
    /// </summary>
    [Fact]
    public async Task steady_state_flood_holds_the_invariant_and_measures_duplicates()
    {
        var leader = await startClusterAsync("Steady");

        var flood = buildFlood();
        var token = TestContext.Current.CancellationToken;
        var pump = Task.Run(() => flood.RunAsync(senderCount: 4, token), token);

        // Even with no chaos, the control run has to be a genuine flood -- otherwise its duplicate rate is
        // the floor for an idle cluster rather than a loaded one.
        var saturated = await waitForSaturatedBacklogAsync(SaturationDepth, 90.Seconds());
        _output.WriteLine($"  saturated at {saturated.Ready} ready, {saturated.Unacknowledged} unacknowledged");

        await pump;

        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(flood.Published, 300.Seconds()))
            .ShouldBeTrue("Not every published webhook event was handled inside the timeout");

        var report = settleAndAssert("steady state (no chaos)", flood, leader, disruptedNodes: 0);

        // The control run is the one place a near-zero duplicate rate is a real claim rather than an
        // accident of scale, so it is asserted and not merely printed.
        report.DuplicateExecutions.ShouldBeLessThanOrEqualTo(report.DocumentedPrefetchBound);
    }

    // ---- phase 2: hard node kill -------------------------------------------------------------------

    /// <summary>
    /// A node dies outright mid-flood, taking its in-flight handlers with it, and a replacement joins. Its
    /// slots have to be reassigned, everything it had unacked has to come back, and the invariant has to hold
    /// across the handoff.
    /// </summary>
    [Fact]
    public async Task hard_node_kill_mid_flood_holds_the_invariant_and_measures_duplicates()
    {
        var leader = await startClusterAsync("Killed");

        var flood = buildFlood();
        var token = TestContext.Current.CancellationToken;
        var pump = Task.Run(() => flood.RunAsync(senderCount: 4, token), token);

        await waitForSaturatedBacklogAsync(SaturationDepth, 90.Seconds());

        // Kill a non-leader that is actually consuming slots, so this is a slot handoff rather than an
        // election.
        var victim = _hosts.Skip(1).First(h => slotAgentsOn(h).Any());
        var victimName = victim.GetRuntime().Options.ServiceName;
        var orphaned = slotAgentsOn(victim);

        _output.WriteLine($"Hard killing {victimName}, which owns {orphaned.Select(x => x.ToString()).Join(", ")}");
        await captureUnackedAtDisruptionAsync();
        await killHostAsync(victim);

        var replacement = await startHostAsync($"KilledReplacement{++_generation}");
        await waitForFullSlotOwnershipAsync(120.Seconds());

        // Processing has to keep going on the survivors, not merely resume owning the slots.
        var before = NativeAckPartitionedProcessing.Ledger.Handled.Count;
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);
        NativeAckPartitionedProcessing.Ledger.Handled.Count.ShouldBeGreaterThan(before,
            "Nothing was processed after the node was killed");

        await pump;

        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(flood.Published, 300.Seconds()))
            .ShouldBeTrue("Events unacked by the killed node were never redelivered and handled");

        settleAndAssert("hard node kill", flood, replacement, disruptedNodes: 1);

        // The kill has to have actually moved work, or none of the above was tested.
        var nodes = NativeAckPartitionedProcessing.Ledger.Handled.Select(x => x.NodeName).Distinct().ToArray();
        nodes.ShouldContain(victimName, "The killed node never handled anything before it died");

        var orphanedQueues = orphaned.Select(x => x.Segments.Last()).ToHashSet();
        NativeAckPartitionedProcessing.Ledger.Handled
            .Where(x => x.NodeName != victimName && orphanedQueues.Contains(x.Destination!.Segments.Last()))
            .ShouldNotBeEmpty("No survivor ever processed a message from one of the reassigned slots");
    }

    // ---- phase 3: rolling deploy -------------------------------------------------------------------

    /// <summary>
    /// The shape the documentation makes its duplicate claim about: a rolling deploy, replacing one node at a
    /// time, gracefully, while the flood keeps arriving. Every node in the cluster is replaced.
    /// </summary>
    [Fact]
    public async Task rolling_deploy_mid_flood_holds_the_invariant_and_measures_duplicates()
    {
        await startClusterAsync("Rolling");

        // Five sequential drain-and-replace cycles take minutes, so the flood has to be big enough that the
        // cluster is still grinding through it when the last node is replaced.
        var flood = buildFlood(sizeMultiplier: 4);
        var token = TestContext.Current.CancellationToken;
        var pump = Task.Run(() => flood.RunAsync(senderCount: 4, token), token);

        await waitForSaturatedBacklogAsync(SaturationDepth, 90.Seconds());

        // Replace the whole cluster one node at a time. The publisher picks from the live set each send, so
        // the flood follows the deploy around rather than dying with the first node drained.
        for (var i = 0; i < NodeCount; i++)
        {
            var outgoing = _hosts[0];
            var outgoingName = outgoing.GetRuntime().Options.ServiceName;

            _output.WriteLine($"Rolling deploy: draining {outgoingName} " +
                              $"(owns {slotAgentsOn(outgoing).Select(x => x.ToString()).Join(", ")})");

            await captureUnackedAtDisruptionAsync();
            await stopHostAsync(outgoing);
            await startHostAsync($"RollingReplacement{++_generation}");
            await waitForFullSlotOwnershipAsync(120.Seconds());
        }

        await pump;

        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(flood.Published, 300.Seconds()))
            .ShouldBeTrue("Not every published webhook event survived the rolling deploy");

        var witness = _hosts[0];
        settleAndAssert("rolling deploy (all 5 nodes replaced)", flood, witness, disruptedNodes: NodeCount);

        // Every original node has to have been replaced, or this was not a rolling deploy.
        _hosts.ShouldAllBe(h => h.GetRuntime().Options.ServiceName.StartsWith("RollingReplacement"));

        // Both generations have to have done real work, otherwise the handoffs were never entered.
        var nodes = NativeAckPartitionedProcessing.Ledger.Handled.Select(x => x.NodeName).Distinct().ToArray();
        nodes.ShouldContain(x => x.StartsWith("Rolling") && !x.StartsWith("RollingReplacement"));
        nodes.ShouldContain(x => x.StartsWith("RollingReplacement"));
    }

    // ---- phase 4: combined chaos -------------------------------------------------------------------

    /// <summary>
    /// The acceptance run: a hard kill and a rolling replacement in the same flood, overlapping. This is the
    /// one the issue asks to pass repeatedly.
    /// </summary>
    [Fact]
    public async Task combined_chaos_flood_holds_the_invariant_cluster_wide()
    {
        await startClusterAsync("Chaos");

        var flood = buildFlood(sizeMultiplier: 4);
        var token = TestContext.Current.CancellationToken;
        var pump = Task.Run(() => flood.RunAsync(senderCount: 4, token), token);

        await waitForSaturatedBacklogAsync(SaturationDepth, 90.Seconds());

        // 1. A node dies outright.
        var victim = _hosts.Skip(1).First(h => slotAgentsOn(h).Any());
        _output.WriteLine($"Combined chaos: hard killing {victim.GetRuntime().Options.ServiceName}");
        await captureUnackedAtDisruptionAsync();
        await killHostAsync(victim);
        await startHostAsync($"ChaosReplacement{++_generation}");
        await waitForFullSlotOwnershipAsync(120.Seconds());

        // 2. Two more are drained and replaced while the flood is still arriving.
        for (var i = 0; i < 2; i++)
        {
            var outgoing = _hosts.Skip(1).First();
            _output.WriteLine($"Combined chaos: draining {outgoing.GetRuntime().Options.ServiceName}");
            await captureUnackedAtDisruptionAsync();
            await stopHostAsync(outgoing);
            await startHostAsync($"ChaosReplacement{++_generation}");
            await waitForFullSlotOwnershipAsync(120.Seconds());
        }

        // 3. And another hard kill on the far side of the deploy.
        var second = _hosts.Skip(1).First(h => slotAgentsOn(h).Any());
        _output.WriteLine($"Combined chaos: hard killing {second.GetRuntime().Options.ServiceName}");
        await captureUnackedAtDisruptionAsync();
        await killHostAsync(second);
        await startHostAsync($"ChaosReplacement{++_generation}");
        await waitForFullSlotOwnershipAsync(120.Seconds());

        await pump;

        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(flood.Published, 420.Seconds()))
            .ShouldBeTrue("Not every published webhook event survived the combined chaos run");

        settleAndAssert("combined chaos (2 kills + 2 rolling replacements)", flood, _hosts[0], disruptedNodes: 4);
    }

    // ---- the storage-free claim --------------------------------------------------------------------

    /// <summary>
    /// The claim that separates this mode from the Durable topology the client was complaining about: the
    /// database carries node coordination and nothing else. Asserted against the incoming table, because
    /// "no database on the message path" is exactly the sort of claim that quietly stops being true.
    /// </summary>
    [Fact]
    public async Task no_webhook_event_ever_touches_the_database()
    {
        await startClusterAsync("Storeless");

        var flood = buildFlood(sizeMultiplier: 1, duration: 20.Seconds());
        await flood.RunAsync(senderCount: 4, TestContext.Current.CancellationToken);

        (await NativeAckPartitionedProcessing.WaitForCompletionAsync(flood.Published, 300.Seconds()))
            .ShouldBeTrue("Not every published webhook event was handled inside the timeout");

        NativeAckPartitionedProcessing.AssertNoIntraGroupConcurrency();
        NativeAckPartitionedProcessing.AssertEveryLetterWasHandled(flood.Published);

        var token = TestContext.Current.CancellationToken;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(token);

        await using var command = conn.CreateCommand();
        command.CommandText = $"select count(*) from {SchemaName}.wolverine_incoming_envelopes";
        var incoming = (long)(await command.ExecuteScalarAsync(token))!;

        await conn.CloseAsync();

        incoming.ShouldBe(0,
            $"{incoming} envelopes reached the durable inbox. The native-ack message path must never write one.");

        // And the nodes table proves the store really was in use -- otherwise the count above is zero for the
        // trivial reason that nothing was configured at all.
        NativeAckPartitionedProcessing.Ledger.Handled.Select(x => x.NodeName).Distinct().Count()
            .ShouldBeGreaterThan(1, "Only one node did any work, so cluster coordination was never exercised");
    }
}
