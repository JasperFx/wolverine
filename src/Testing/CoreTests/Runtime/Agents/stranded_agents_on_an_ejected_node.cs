using System.Collections.Concurrent;
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
/// A node that dies ungracefully leaves its node row and every one of its assignment rows in place until
/// <c>StaleNodeTimeout</c> elapses. The dead pod restarts quickly and the leader rebalances across N+1 nodes.
/// One of those nodes is still the dead node, not yet removed so some agents are now down.
///
/// <para>That goes out as a <see cref="ReassignAgents" /> in the dead node's own lane. Its stop can never be
/// acknowledged, so the agents never cascade into a start anywhere, while the pending-assignment ledger
/// reports the move as outstanding for the whole of the batch's reply window — over twenty minutes for a
/// chunk of forty agents. Every later evaluation therefore counts those agents as already placed: they are
/// not in the distribution's missing queue, they inflate the destination's share, and no command is emitted
/// for them. The cluster runs short with <c>assigned == running</c> agreed on both sides and
/// nothing logged.</para>
///
/// <para>Measured on a 3-node, 500-agent cluster: exactly <c>166 - ceil(500/4) = 41</c> agents stranded on
/// PostgreSQL and 42 on RavenDB, on every released build from 6.35.0 forward. The count is a ceiling
/// division, not a race.</para>
/// </summary>
public class stranded_agents_on_an_ejected_node
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly INodeAgentPersistence _persistence = Substitute.For<INodeAgentPersistence>();
    private readonly FakeAgentFamily _family = new("fake", 40);
    private NodeAgentController _controller;
    private readonly RecordingLogger _logger = new();

    private readonly WolverineNode _leader;
    private readonly WolverineNode _corpse;
    private readonly WolverineNode _newcomer;

    public stranded_agents_on_an_ejected_node()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;

        // The ledger's TTL backstop is 2 x this. Kept tiny throughout so nothing below is ever held by the
        // clock -- the only thing that can hold a move here is the dispatcher saying it is still working it,
        // which is exactly the hold this bug turns into a permanent one.
        _options.Durability.CheckAssignmentPeriod = 10.Milliseconds();

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        _controller = new NodeAgentController(
            _runtime, _persistence, [_family], _logger, CancellationToken.None);

        _leader = nodeFor(_options.UniqueNodeId, 1, "fake://self");
        _corpse = nodeFor(Guid.NewGuid(), 2, "fake://corpse");
        _newcomer = nodeFor(Guid.NewGuid(), 3, "fake://newcomer");

        // The steady state before the kill: two nodes holding everything between them.
        var all = _family.AllAgentUris();
        _leader.ActiveAgents.AddRange(all.Take(all.Length / 2));
        _corpse.ActiveAgents.AddRange(all.Skip(all.Length / 2));
    }

    /// <summary>
    /// Put a second family in play. The leader evaluates each family in turn, so anything reported once per
    /// evaluation has to survive that loop without being multiplied by it.
    /// </summary>
    private FakeAgentFamily addASecondFamily()
    {
        var second = new FakeAgentFamily("other", 8);

        _controller = new NodeAgentController(
            _runtime, _persistence, [_family, second], _logger, CancellationToken.None);

        foreach (var node in new[] { _leader, _corpse, _newcomer })
        {
            node.Capabilities.AddRange(second.AllAgentUris());
        }

        return second;
    }

    private WolverineNode nodeFor(Guid id, int number, string controlUri)
    {
        var node = new WolverineNode
        {
            NodeId = id,
            AssignedNodeNumber = number,
            ControlUri = controlUri.ToUri()
        };

        node.Capabilities.AddRange(_family.AllAgentUris());
        return node;
    }

    private Task<AgentCommands> evaluateAsync(params WolverineNode[] nodes)
        => _controller.EvaluateAssignmentsAsync(nodes, new AgentRestrictions());

    private static Uri[] agentsIn(AgentCommands commands)
        => commands.OfType<AssignAgents>().SelectMany(x => x.AgentIds)
            .Concat(commands.OfType<AssignAgent>().Select(x => x.AgentUri))
            .Concat(commands.OfType<ReassignAgents>().SelectMany(x => x.AgentUris))
            .Concat(commands.OfType<ReassignAgent>().Select(x => x.AgentUri))
            .ToArray();

    /// <summary>
    /// The rebalance that starts the whole thing: while the corpse is still fresh enough to be in the node
    /// list it takes a full share of the ceiling, so agents are moved off a node that cannot answer.
    /// </summary>
    private async Task<Uri[]> moveAgentsOffTheCorpseAsync()
    {
        var commands = await evaluateAsync(_leader, _corpse, _newcomer);

        var offTheCorpse = commands.OfType<ReassignAgents>()
            .Where(x => x.OriginalNode.NodeId == _corpse.NodeId)
            .SelectMany(x => x.AgentUris)
            .ToArray();

        offTheCorpse.ShouldNotBeEmpty();

        // The moves whose source is alive land normally; only the corpse's are left in the air.
        foreach (var moved in commands.OfType<ReassignAgents>().Where(x => x.OriginalNode.NodeId == _leader.NodeId))
        {
            foreach (var uri in moved.AgentUris)
            {
                _leader.ActiveAgents.Remove(uri);
                _newcomer.ActiveAgents.Add(uri);
            }
        }

        // The dispatcher is parked on the corpse's reply window, so it keeps reporting the move as live.
        _controller.PendingDispatches = (Uri uri, out Guid nodeId) =>
        {
            nodeId = _newcomer.NodeId;
            return offTheCorpse.Contains(uri);
        };

        return offTheCorpse;
    }

    /// <summary>
    /// The shape of the bug, and the reason the fix has to be a release rather than a shorter timeout: the
    /// ledger is doing exactly what it was built to do. While the dispatcher says the move is still being
    /// worked, the leader holds the agents on their destination and emits nothing — even after the source
    /// node has left the cluster entirely.
    /// </summary>
    [Fact]
    public async Task a_move_off_a_dead_node_is_held_while_the_dispatcher_still_claims_it()
    {
        var stranded = await moveAgentsOffTheCorpseAsync();

        // The corpse is ejected and drops out of the node list, but nothing tells the dispatcher to let go.
        var commands = await evaluateAsync(_leader, _newcomer);

        agentsIn(commands).ShouldNotContain(stranded[0]);
        agentsIn(commands).Intersect(stranded).ShouldBeEmpty();
    }

    /// <summary>
    /// The fix. Once the corpse is out of the roster the leader has nothing left to wait on: the commands
    /// aimed at it are abandoned, the ledger forgets what they were holding, and the agents are placed by the
    /// very next evaluation — the same one that re-places every other agent the departed node owned.
    /// </summary>
    [Fact]
    public async Task dropping_out_of_the_roster_releases_the_agents_its_commands_were_carrying()
    {
        var stranded = await moveAgentsOffTheCorpseAsync();

        // A TTL far longer than this test can wait, so the ledger's own backstop cannot be what frees these
        // agents. Releasing the dispatcher's claim alone would only downgrade the hold from "outstanding" to
        // "within the TTL" -- a minute on default settings -- and the agents would still be silently held on
        // a destination they never reached. Only forgetting the entry places them now.
        _options.Durability.CheckAssignmentPeriod = 5.Minutes();

        // Stands in for AgentCommandDispatcher.AbandonLanesExcept: the departed node's claims are released
        // and it reports which agents that freed, each against the node the ledger is holding it on.
        _controller.AbandonDispatchesOutside = (registered, _) =>
        {
            registered.ShouldNotContain(_corpse.NodeId);
            _controller.PendingDispatches = null;
            return stranded.Select(uri => (uri, _newcomer.NodeId)).ToArray();
        };

        _controller.AbandonDispatchesExcept(rosterOf(_leader, _newcomer));

        var commands = await evaluateAsync(_leader, _newcomer);

        foreach (var uri in stranded)
        {
            agentsIn(commands).ShouldContain(uri);
        }
    }

    /// <summary>
    /// The reason the release is driven by the roster and not by the ejection. Ejecting a stale row is not
    /// the leader's privilege — <c>ejectStaleNodes</c> spares only the current leader's row — so a follower
    /// commonly wins the race and runs the release against a dispatcher holding nothing. Measured on a
    /// three-node cluster, that left the leader stranded for the full reply window twice running. The leader
    /// asks the question of its own membership view instead, so whoever performed the delete is irrelevant.
    /// </summary>
    [Fact]
    public async Task releases_a_node_that_some_other_node_ejected()
    {
        var stranded = await moveAgentsOffTheCorpseAsync();
        _options.Durability.CheckAssignmentPeriod = 5.Minutes();

        // Nothing on this leader ejected anything; the corpse has simply gone from the roster it reads.
        var asked = new List<IReadOnlySet<Guid>>();
        _controller.AbandonDispatchesOutside = (registered, _) =>
        {
            asked.Add(registered);
            _controller.PendingDispatches = null;
            return stranded.Select(uri => (uri, _newcomer.NodeId)).ToArray();
        };

        _controller.AbandonDispatchesExcept(rosterOf(_leader, _newcomer));

        asked.Single().ShouldBe(rosterOf(_leader, _newcomer));

        var commands = await evaluateAsync(_leader, _newcomer);
        foreach (var uri in stranded)
        {
            agentsIn(commands).ShouldContain(uri);
        }
    }

    /// <summary>
    /// A released claim only ends the wait it was actually backing. The ledger frees an entry as soon as its
    /// destination leaves the grid, while the command holding the claim stays wedged in some other departed
    /// node's lane, so by the time that lane is abandoned the ledger can be holding the same agent for a
    /// newer placement somewhere else. Dropping that entry on the strength of the older claim leaves the
    /// agent with no hold at all, and the next evaluation starts a second copy with no stop for the first.
    /// </summary>
    [Fact]
    public async Task does_not_drop_a_ledger_hold_that_names_a_different_destination()
    {
        var stranded = await moveAgentsOffTheCorpseAsync();
        _options.Durability.CheckAssignmentPeriod = 5.Minutes();

        // The ledger is waiting on the newcomer for these; the abandoned lane reports a claim it took out
        // back when the corpse was the destination.
        _controller.AbandonDispatchesOutside =
            (_, _) => stranded.Select(uri => (uri, _corpse.NodeId)).ToArray();

        _controller.AbandonDispatchesExcept(rosterOf(_leader, _newcomer));

        var commands = await evaluateAsync(_leader, _newcomer);

        agentsIn(commands).Intersect(stranded).ShouldBeEmpty();
    }

    /// <summary>
    /// Abandoning a lane cancels the command running in it, so the leader asks for the same sustained
    /// absence that the destructive ejection of a node row requires.
    /// </summary>
    [Fact]
    public void asks_for_the_stale_node_ejection_threshold()
    {
        _options.Durability.StaleNodeEjectionThreshold = 3;

        var asked = new List<int>();
        _controller.AbandonDispatchesOutside = (_, threshold) =>
        {
            asked.Add(threshold);
            return [];
        };

        _controller.AbandonDispatchesExcept(rosterOf(_leader, _newcomer));

        asked.Single().ShouldBe(3);
    }

    private static IReadOnlySet<Guid> rosterOf(params WolverineNode[] nodes)
        => nodes.Select(x => x.NodeId).ToHashSet();

    /// <summary>
    /// Nothing else in the system can report this: an agent held here runs nowhere and holds no assignment
    /// row, so the grid, the node-side reconciliation sweep and anything reading the assignment table all
    /// agree the cluster is healthy and merely smaller. Past the point where the cluster itself would call a
    /// batch wedged, the leader says so.
    /// </summary>
    [Fact]
    public async Task warns_when_assignments_are_held_pending_past_the_stall_timeout()
    {
        _options.Durability.AgentProgressStallTimeout = 20.Milliseconds();

        await moveAgentsOffTheCorpseAsync();

        _logger.Warnings.ShouldBeEmpty();

        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        await evaluateAsync(_leader, _newcomer);

        _logger.Warnings.ShouldContain(x => x.Contains("held pending for longer than"));
    }

    /// <summary>
    /// One stall, one warning. The warning used to be emitted from the per-family pass, so a cluster with N
    /// families logged the same stall N times over on every evaluation.
    /// </summary>
    [Fact]
    public async Task warns_once_per_evaluation_however_many_families_there_are()
    {
        _options.Durability.AgentProgressStallTimeout = 20.Milliseconds();

        addASecondFamily();

        await moveAgentsOffTheCorpseAsync();
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        await evaluateAsync(_leader, _newcomer);

        _logger.Warnings.Count(x => x.Contains("held pending for longer than")).ShouldBe(1);
    }

    /// <summary>
    /// A stall that persists is a standing condition, not news on every health check. Evaluations run every
    /// HealthCheckPollingTime, which is far shorter than the threshold that makes a hold worth reporting.
    /// </summary>
    [Fact]
    public async Task does_not_repeat_the_warning_on_every_evaluation()
    {
        _options.Durability.AgentProgressStallTimeout = 20.Milliseconds();

        await moveAgentsOffTheCorpseAsync();
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        await evaluateAsync(_leader, _newcomer);
        await evaluateAsync(_leader, _newcomer);
        await evaluateAsync(_leader, _newcomer);

        _logger.Warnings.Count(x => x.Contains("held pending for longer than")).ShouldBe(1);
    }

    /// <summary>
    /// The warning is about a hold that has gone on too long, not about holding at all — a wave of slow
    /// starts is the case the ledger exists to serve and must stay quiet.
    /// </summary>
    [Fact]
    public async Task does_not_warn_about_a_dispatch_that_is_merely_in_progress()
    {
        await moveAgentsOffTheCorpseAsync();

        await evaluateAsync(_leader, _corpse, _newcomer);

        _logger.Warnings.ShouldBeEmpty();
    }

    private class RecordingLogger : ILogger<NodeAgentController>
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
