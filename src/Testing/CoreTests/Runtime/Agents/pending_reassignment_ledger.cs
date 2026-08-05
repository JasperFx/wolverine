using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3852. The pending-assignment ledger (GH-3698) closed the re-decision hole for first-time assignments
/// but silently excluded every reassignment: an agent being moved is still listed in its SOURCE node's
/// persisted ActiveAgents, so <c>Agent.OriginalNode</c> is set, and <c>applyPendingAssignments</c> skipped
/// it outright. The ledger armed on a <see cref="ReassignAgent" /> and could never apply one, so the leader
/// re-decided the same move from scratch on every evaluation until the source's assignment row finally
/// disappeared — one <c>AssignmentChanged</c> row per moved agent per cycle, plus a redundant
/// <c>StopAgents</c> round trip once the dispatcher's in-flight hold had been released.
///
/// <para>Reported from a 512-database, 5-node cluster: ~45,000 reassignment decisions in a six-minute ramp
/// window against ~8,700 agents.</para>
/// </summary>
public class pending_reassignment_ledger
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly INodeAgentPersistence _persistence = Substitute.For<INodeAgentPersistence>();
    private readonly FakeAgentFamily _family = new("fake", 40);
    private readonly NodeAgentController _controller;
    private readonly WolverineNode _incumbent;
    private readonly WolverineNode _newcomer;

    public pending_reassignment_ledger()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        _controller = new NodeAgentController(
            _runtime, _persistence, [_family], NullLogger<NodeAgentController>.Instance, CancellationToken.None);

        _incumbent = new WolverineNode
        {
            NodeId = _options.UniqueNodeId,
            AssignedNodeNumber = 1,
            ControlUri = _options.Transports.NodeControlEndpoint!.Uri
        };
        _incumbent.Capabilities.AddRange(_family.AllAgentUris());

        _newcomer = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            AssignedNodeNumber = 2,
            ControlUri = "fake://two".ToUri()
        };
        _newcomer.Capabilities.AddRange(_family.AllAgentUris());
    }

    private Task<AgentCommands> evaluateAsync()
        => _controller.EvaluateAssignmentsAsync([_incumbent, _newcomer], new AgentRestrictions());

    private static int reassignedAgentCount(AgentCommands commands)
        => commands.OfType<ReassignAgents>().Sum(x => x.AgentUris.Length)
           + commands.OfType<ReassignAgent>().Count();

    /// <summary>
    /// The ramp shape: one node holding everything, a newcomer holding nothing. The rebalance is decided
    /// once; while the moves are still executing, an unchanged node-state snapshot must decide nothing.
    /// </summary>
    [Fact]
    public async Task suppresses_re_emission_of_a_dispatched_reassignment()
    {
        _incumbent.ActiveAgents.AddRange(_family.AllAgentUris());

        // Nothing is outstanding at the dispatcher, so the hold this first evaluation gets is the ledger's
        // TTL backstop -- long enough for the point of this test, and the dispatcher's own signal is
        // covered by holds_the_move_for_as_long_as_the_dispatcher_is_still_working_it below.
        var first = await evaluateAsync();
        var moved = reassignedAgentCount(first);
        moved.ShouldBe(_family.AllAgentUris().Length / 2);

        // The newcomer has not started them yet and the incumbent has not persisted their removal, so the
        // snapshot is byte-for-byte what the first evaluation saw. Pre-fix this re-decided all 20 every time.
        for (var cycle = 0; cycle < 5; cycle++)
        {
            reassignedAgentCount(await evaluateAsync()).ShouldBe(0);
        }
    }

    /// <summary>
    /// The suppression must not survive the dispatch failing. Once the move stops being outstanding and the
    /// TTL lapses without the agent turning up on its destination, the leader has to drive it again.
    /// </summary>
    [Fact]
    public async Task re_drives_a_reassignment_that_is_never_confirmed()
    {
        _options.Durability.CheckAssignmentPeriod = 10.Milliseconds();
        _incumbent.ActiveAgents.AddRange(_family.AllAgentUris());

        var moved = reassignedAgentCount(await evaluateAsync());
        moved.ShouldBeGreaterThan(0);

        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken); // past the 20ms TTL

        reassignedAgentCount(await evaluateAsync()).ShouldBe(moved);
    }

    /// <summary>
    /// A move whose command is still executing must be held on the dispatcher's signal, not the clock. The
    /// ledger TTL is 2 x CheckAssignmentPeriod (60s by default) while a reassignment batch's own reply window
    /// runs to tens of minutes, so without <c>AgentCommandDispatcher._moving</c> the leader resumes
    /// re-deciding the move long before the command it is waiting on could possibly have finished.
    /// </summary>
    [Fact]
    public async Task holds_the_move_for_as_long_as_the_dispatcher_is_still_working_it()
    {
        // TTL of 20ms: expired many times over by the time the second evaluation runs. Only the dispatcher's
        // in-flight signal can hold the move now.
        _options.Durability.CheckAssignmentPeriod = 10.Milliseconds();
        _incumbent.ActiveAgents.AddRange(_family.AllAgentUris());

        var destination = Guid.Empty;
        _controller.PendingDispatches = (Uri _, out Guid nodeId) =>
        {
            nodeId = destination;
            return true;
        };

        var first = await evaluateAsync();
        reassignedAgentCount(first).ShouldBeGreaterThan(0);
        destination = first.OfType<ReassignAgents>().Single().ActiveNode.NodeId;

        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        reassignedAgentCount(await evaluateAsync()).ShouldBe(0);
    }

    /// <summary>
    /// Confirmation still ends the wait: once the destination reports the agents running, a later loss has to
    /// be re-decided immediately rather than sitting out a TTL. Mirrors
    /// <c>pending_assignment_ledger.confirmation_clears_the_ledger_so_a_later_loss_re_emits</c>.
    /// </summary>
    [Fact]
    public async Task confirmation_on_the_destination_clears_the_ledger()
    {
        _incumbent.ActiveAgents.AddRange(_family.AllAgentUris());

        var first = await evaluateAsync();
        var movedUris = first.OfType<ReassignAgents>().Single().AgentUris;

        // The moves land: the newcomer is running them and the incumbent has let them go.
        foreach (var uri in movedUris) _incumbent.ActiveAgents.Remove(uri);
        _newcomer.ActiveAgents.AddRange(movedUris);

        // Settled state, and a fixed point: nothing to decide.
        reassignedAgentCount(await evaluateAsync()).ShouldBe(0);

        // The newcomer now drops out of the cluster entirely. Because confirmation CLEARED the ledger rather
        // than merely suppressing it, the agents are placed again at once.
        var commands = await _controller.EvaluateAssignmentsAsync([_incumbent], new AgentRestrictions());
        commands.OfType<AssignAgents>().Sum(x => x.AgentIds.Length).ShouldBe(movedUris.Length);
    }

    /// <summary>
    /// An operator pause outranks a dispatch the leader has not managed to complete — the same precedence
    /// <c>applyPendingAssignments</c> gives a pending first-time assignment. The suppression must never
    /// swallow a stop.
    /// </summary>
    [Fact]
    public async Task a_pause_still_stops_an_agent_with_a_move_in_flight()
    {
        _incumbent.ActiveAgents.AddRange(_family.AllAgentUris());

        var first = await evaluateAsync();
        var moving = first.OfType<ReassignAgents>().Single().AgentUris[0];

        var restrictions = new AgentRestrictions();
        restrictions.PauseAgent(moving);

        var commands = await _controller.EvaluateAssignmentsAsync([_incumbent, _newcomer], restrictions);
        commands.OfType<StopRemoteAgent>().ShouldContain(x => x.AgentUri == moving);
    }

    /// <summary>
    /// The reported cluster: 512 databases x 17 agents across five nodes, group affinity by database, three
    /// incumbents holding everything and two newcomers ramping in. Pre-fix this decided 3,468 reassignments
    /// on EVERY cycle against a frozen snapshot — roughly 13 cycles of which is the reported ~45,000, each
    /// one an AssignmentChanged row into the node-record table (#3658).
    /// </summary>
    [Fact]
    public async Task the_production_ramp_shape_decides_its_rebalance_exactly_once()
    {
        const int databases = 512;
        const int agentsPerDatabase = 17;

        var names = new List<string>();
        for (var d = 0; d < databases; d++)
        for (var a = 0; a < agentsPerDatabase; a++)
            names.Add($"db{d:D3}/agent{a:D2}");

        var family = new FakeAgentFamily("fake", names)
        {
            Distribution = grid => grid.DistributeByGroupAffinity("fake", uri => uri.Host)
        };

        var controller = new NodeAgentController(
            _runtime, _persistence, [family], NullLogger<NodeAgentController>.Instance, CancellationToken.None);

        var all = family.AllAgentUris();
        all.Length.ShouldBe(databases * agentsPerDatabase);

        var nodes = new List<WolverineNode>();
        for (var i = 0; i < 5; i++)
        {
            var node = new WolverineNode
            {
                NodeId = i == 0 ? _options.UniqueNodeId : Guid.NewGuid(),
                AssignedNodeNumber = i + 1,
                ControlUri = i == 0 ? _options.Transports.NodeControlEndpoint!.Uri : $"fake://node{i}".ToUri()
            };
            node.Capabilities.AddRange(all);
            nodes.Add(node);
        }

        // Incumbents 0-2 hold everything, split by database; newcomers 3-4 hold nothing.
        for (var d = 0; d < databases; d++)
        {
            var owner = nodes[d % 3];
            for (var a = 0; a < agentsPerDatabase; a++)
            {
                owner.ActiveAgents.Add(new Uri($"fake://db{d:D3}/agent{a:D2}"));
            }
        }

        var first = await controller.EvaluateAssignmentsAsync(nodes, new AgentRestrictions());
        reassignedAgentCount(first).ShouldBeGreaterThan(0);

        // Five more cycles against a snapshot that has not moved: the ramp is slow, not stuck.
        for (var cycle = 0; cycle < 5; cycle++)
        {
            reassignedAgentCount(await controller.EvaluateAssignmentsAsync(nodes, new AgentRestrictions()))
                .ShouldBe(0);
        }
    }

    /// <summary>
    /// The dispatcher's own guard, which is what keeps a re-decision from costing a redundant StopAgents
    /// round trip while the previous copy is still executing. Independent of the ledger above: the ledger
    /// stops the decision being made, this stops an identical one that slips through being executed twice.
    /// </summary>
    [Fact]
    public async Task the_dispatcher_collapses_an_identical_reassignment_still_in_flight()
    {
        _incumbent.ActiveAgents.AddRange(_family.AllAgentUris());

        var executed = new List<IAgentCommand>();
        await using var dispatcher = new AgentCommandDispatcher(
            async (c, _) =>
            {
                lock (executed) executed.Add(c);
                await Task.Delay(5.Seconds());
                return (AgentCommands?)null;
            },
            NullLogger.Instance, CancellationToken.None);

        var commands = await evaluateAsync();
        foreach (var command in commands) dispatcher.Enqueue(command);
        foreach (var command in commands) dispatcher.Enqueue(command);

        await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);

        lock (executed) executed.Count.ShouldBe(1);
    }

    /// <summary>
    /// A reassignment in flight has to report as outstanding to the leader's pending-dispatch probe, keyed to
    /// the node the agents are moving TO — which is not the command's lane (that is the source).
    /// </summary>
    [Fact]
    public async Task an_in_flight_reassignment_reports_its_destination_as_pending()
    {
        _incumbent.ActiveAgents.AddRange(_family.AllAgentUris());

        await using var dispatcher = new AgentCommandDispatcher(
            async (_, _) =>
            {
                await Task.Delay(5.Seconds());
                return (AgentCommands?)null;
            },
            NullLogger.Instance, CancellationToken.None);

        var commands = await evaluateAsync();
        var batch = commands.OfType<ReassignAgents>().Single();
        foreach (var command in commands) dispatcher.Enqueue(command);

        dispatcher.TryFindPendingDestination(batch.AgentUris[0], out var nodeId).ShouldBeTrue();
        nodeId.ShouldBe(batch.ActiveNode.NodeId);
        nodeId.ShouldNotBe(batch.OriginalNode.NodeId);
    }
}
