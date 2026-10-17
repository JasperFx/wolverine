using System.Collections.Concurrent;
using Bobcat;
using Bobcat.Xunit;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests.Telemetry;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
namespace Wolverine.ComplianceTests;

/// <summary>
///     The leadership-election compliance battery every persistence provider and control-queue transport runs, as
///     Bobcat specifications. Each provider's subclass is bodyless and names its own feature with
///     <c>[BobcatFeature("…")]</c>; the steps are on <see cref="LeadershipSteps" />.
/// </summary>
/// <remarks>
///     When a specification fails, it says what the cluster did as well as what it got wrong: a timeline of every
///     node event, leader command and agent start/stop (<see cref="AgentAssignmentTimeline" />), the placement of
///     every agent when the wait gave up (<see cref="AgentPlacementReport" />), the wait's own narration as step
///     logs, and the full transcript as an attachment (<see cref="ClusterTranscriptCapture" />). A passing one writes
///     none of it.
/// </remarks>
public abstract class LeadershipElectionCompliance : IAsyncLifetime
{
    static LeadershipElectionCompliance()
    {
        // Once per process; registering the same instance again is one registration
        SpecAttachment.RegisterFailureCapture(ClusterTranscriptCapture.Instance);
    }

    protected readonly LeadershipSteps the;
    private ClusterNode _first = null!;

    // TODO -- rename this after combining
    protected abstract Task beforeBuildingHost();

    public LeadershipElectionCompliance(ITestOutputHelper output)
    {
        the = new LeadershipSteps(GetType(), opts => configureNode(opts), output);
    }

    public async ValueTask InitializeAsync()
    {
        await beforeBuildingHost();

        // Before the specification's recording opens (xUnit v3 runs InitializeAsync first), so this is not a
        // step; the telemetry is already buffering, and the first step attaches it to the scenario
        _first = await the.StartNodeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await the.DisposeAsync();

        await AfterDisposeAsync();
    }

    /// <summary>
    /// Template method for transport-specific cleanup after hosts are disposed.
    /// </summary>
    protected virtual Task AfterDisposeAsync()
    {
        return Task.CompletedTask;
    }

    public IHost FindHostRunning(Uri agentUri) => the.NodeRunning(agentUri).Host;

    public IHost OriginalHost => _first.Host;

    protected abstract void configureNode(WolverineOptions options);

    [BobcatSpec]
    public async Task the_only_known_node_is_automatically_the_leader()
    {
        await the.NodeBecomesTheLeader(_first);
    }

    /***** NEW TESTS START HERE **********************************************/

    [BobcatSpec]
    public async Task the_original_node_knows_about_all_the_possible_agents()
    {
        await the.AgentsSettleAs(new Placement { [_first] = 12 });
    }

    [BobcatSpec]
    public async Task add_second_node_see_balanced_nodes()
    {
        var second = await the.ANodeStarts();

        await the.AgentsSettleAs(new Placement { [_first] = 6, [second] = 6 });
    }

    /// <summary>
    ///     Health checks are disabled for the rest of this test on purpose. The health-check loop re-reads
    ///     restrictions from the database and would eventually stop the agent anyway, which would mask the defect:
    ///     GH-698 / CritterWatch#698 is that ApplyRestrictionsAsync persisted the restriction and then DISCARDED the
    ///     StopRemoteAgent that EvaluateAssignmentsAsync returned, so the pause had no *immediate* effect. Isolating
    ///     that path is the whole point.
    /// </summary>
    [BobcatSpec]
    public async Task pausing_an_agent_stops_it_immediately()
    {
        await the.AgentsSettleAs(new Placement { [_first] = 12 });

        the.NodeStopsRunningHealthChecks(_first);

        var agent = the.AnAgentRunningOn(_first);
        await the.AgentIsPaused(agent, _first);

        the.AgentHasStoppedAndIsKnownPaused(agent, _first);
    }

    /// <summary>
    /// GH-3698. Puts the two situations the pending-assignment work exists for into one scenario: an
    /// operator pause landing while assignments are in flight, and then the nodes holding the rest of them
    /// disappearing — the leader among them, so a fresh election has to carry the restriction forward too.
    /// Neither may lose an agent, and neither may leave one running twice.
    ///
    /// <para>Before the fix, re-placing a dispatched-but-unconfirmed agent produced a bare start on the new
    /// node with no stop for the copy already coming up on the old one, and pausing such an agent produced
    /// no command at all — there was no <c>OriginalNode</c> to stop it on, so the in-flight start came up and
    /// ran straight through the pause.</para>
    /// </summary>
    [BobcatSpec]
    public async Task every_agent_survives_a_pause_and_losing_the_nodes_that_held_them()
    {
        await the.NodeBecomesTheLeader(_first);

        var second = await the.ANodeStarts();
        var third = await the.ANodeStarts();

        // Settle first, so the agents about to be moved are ones the cluster genuinely placed rather than
        // ones that were never assigned in the first place.
        await the.EveryAgentRunsExactlyOnce();

        var paused = new Uri("fake://twelve").ToString();
        await the.AgentIsPaused(paused, _first);

        // Take away the leader and one more, leaving a single survivor that has to pick up everything.
        await the.NodeStops(_first);
        await the.NodeStops(second);

        await the.EveryAgentButThePausedOneRunsExactlyOnce(paused);

        // ...and the survivor really is the only node left holding them.
        the.NodeRuns(third, LeadershipSteps.AllFakeAgents.Select(x => x.ToString()).First(x => x != paused));
    }

    // GH-4407: the divergences the GH-3987 reconciliation sweep exists to heal, each fabricated directly on a
    // real cluster against real storage. Every provider has to come back to exactly one copy of every agent.

    [BobcatSpec]
    public async Task a_copy_on_a_node_the_table_does_not_name_converges_to_one_copy_with_a_matching_row()
    {
        await the.NodeBecomesTheLeader(_first);
        await the.ANodeStarts();
        await the.EveryAgentRunsExactlyOnce();

        var agent = LeadershipSteps.AllFakeAgents[0].ToString();
        var intruder = the.Nodes.First(x => x != the.NodeRunning(agent));

        // The intruder's sweep stops its copy without touching the owner's row; on a store whose delete was not
        // owner-scoped that stop took the row too and the cluster kept re-placing the agent. The store half of that
        // is pinned deterministically by NodePersistenceCompliance.removing_an_assignment_owned_by_another_node_is_a_no_op.
        await the.ANodeStartsACopyNoRowAccountsFor(intruder, agent);

        await the.AgentRunsOnceWithARowNamingItsNode(agent);
        await the.EveryAgentRunsExactlyOnce();
    }

    [BobcatSpec]
    public async Task an_agent_assigned_to_a_node_but_not_running_there_ends_up_running_again()
    {
        await the.NodeBecomesTheLeader(_first);
        await the.ANodeStarts();
        await the.EveryAgentRunsExactlyOnce();

        var agent = LeadershipSteps.AllFakeAgents[0].ToString();

        // The leader reads it as placed and never places it again. The owner's own sweep restarts it -- unless the
        // leader gets there first and re-places it, which is just as good.
        await the.AnAgentStopsLeavingItsRow(agent, the.NodeRunning(agent));

        await the.AgentRunsOnceWithARowNamingItsNode(agent);
        await the.EveryAgentRunsExactlyOnce();
    }

    [BobcatSpec]
    public async Task a_running_agent_whose_row_is_lost_ends_up_running_once_with_a_row_naming_its_node()
    {
        await the.NodeBecomesTheLeader(_first);
        await the.ANodeStarts();
        await the.EveryAgentRunsExactlyOnce();

        var agent = LeadershipSteps.AllFakeAgents[0].ToString();

        // The leader now reads the agent as unplaced and may place it again while the owner's sweep restores its
        // claim -- either way the cluster has to come back to one copy and one row naming the node that runs it.
        await the.AnAgentsRowIsLost(the.NodeRunning(agent), agent);

        await the.AgentRunsOnceWithARowNamingItsNode(agent);
        await the.EveryAgentRunsExactlyOnce();
    }

    [BobcatSpec]
    public async Task every_agent_runs_exactly_once_after_the_leader_dies_with_its_starts_in_flight()
    {
        await the.NodeBecomesTheLeader(_first);

        // New nodes join and the leader starts rebalancing onto them -- then dies before those starts have
        // settled, with no chance to finish or retract them. The next leader inherits whatever landed.
        await the.ANodeStarts();
        await the.ANodeStarts();
        await the.NodeStops(_first);

        await the.EveryAgentRunsExactlyOnce();
    }

    /***** NEW TESTS END HERE **********************************************/

    [BobcatSpec]
    public async Task singular_agent_is_only_running_on_one()
    {
        await the.ANodeStarts();
        await the.ANodeStarts();
        await the.ANodeStarts();

        await the.NodeBecomesTheLeader(_first);

        await the.NodeStops(_first);

        await the.TheSingularAgentRunsOnExactlyOneNode();
    }

    /// <summary>
    /// GH-4569. A node configured as not eligible for leadership runs agents like any other but never takes
    /// the leadership lock, even when it is the one prodded after the leader goes stale; an eligible peer does.
    /// </summary>
    [BobcatSpec]
    public async Task a_node_that_is_not_eligible_for_leadership_never_takes_it()
    {
        await the.NodeBecomesTheLeader(_first);

        var ineligible = await the.AnIneligibleNodeStarts();
        var eligible = await the.ANodeStartsWithASlowHeartbeat();

        // It still takes its share of the agents
        await the.AgentsSettleAs(new Placement { [_first] = 4, [ineligible] = 4, [eligible] = 4 });

        await the.NodeGoesStale(_first);

        // Prodded first, the ineligible node declines the vacant leadership
        await the.NodeChecksAgentHealth(ineligible);
        await the.NodeChecksAgentHealth(ineligible);
        the.NodeIsNotTheLeader(ineligible);

        await the.NodeChecksAgentHealth(eligible);
        await the.NodeBecomesTheLeader(eligible);

        await the.AgentsSettleAs(new Placement { [ineligible] = 6, [eligible] = 6 });
    }

    /// <summary>
    /// GH-4569. A warm-up node of a blue/green rollout never leads, like a node that is not eligible, and on top
    /// of that it is given none of the cluster's ordinary agents: it only ever runs the event subscription agents
    /// that no serving node declares, and this cluster has none.
    /// </summary>
    [BobcatSpec]
    public async Task a_warmup_node_never_leads_and_takes_no_ordinary_agents()
    {
        await the.NodeBecomesTheLeader(_first);

        var warmup = await the.AWarmupNodeStarts();
        var serving = await the.ANodeStartsWithASlowHeartbeat();

        await the.AgentsSettleAs(new Placement { [_first] = 6, [warmup] = 0, [serving] = 6 });

        await the.NodeGoesStale(_first);

        // Prodded first, the warm-up node declines the vacant leadership
        await the.NodeChecksAgentHealth(warmup);
        await the.NodeChecksAgentHealth(warmup);
        the.NodeIsNotTheLeader(warmup);

        await the.NodeChecksAgentHealth(serving);
        await the.NodeBecomesTheLeader(serving);

        await the.AgentsSettleAs(new Placement { [warmup] = 0, [serving] = 12 });
    }

    [BobcatSpec]
    public async Task leader_switchover_between_nodes()
    {
        await the.NodeBecomesTheLeader(_first);
        var second = await the.ANodeStarts();
        var third = await the.ANodeStartsWithASlowHeartbeat();
        await the.ANodeStartsWithASlowHeartbeat();

        await the.NodeStops(_first);

        await the.NodeBecomesTheLeader(second);

        await the.NodeStops(second);

        // The third node has a 10-minute heartbeat, so trigger a health check
        // explicitly to acquire the lock after the second releases it.
        await the.NodeChecksAgentHealth(third);
        await the.NodeBecomesTheLeader(third);
    }

    [BobcatSpec]
    public async Task spin_up_several_nodes_take_away_original_node()
    {
        await the.NodeBecomesTheLeader(_first);

        var second = await the.ANodeStarts();
        var third = await the.ANodeStarts();
        var fourth = await the.ANodeStarts();

        // This is just to eliminate some errors in test output
        await the.AgentsSettleAs(new Placement { [_first] = 3, [second] = 3, [third] = 3, [fourth] = 3 });

        await the.NodeStops(_first);

        await the.AgentsSettleAs(new Placement { [second] = 4, [third] = 4, [fourth] = 4 });
    }

    [BobcatSpec]
    public async Task spin_up_several_nodes_take_away_non_leader_node()
    {
        await the.NodeBecomesTheLeader(_first);

        var second = await the.ANodeStarts();
        var third = await the.ANodeStarts();
        var fourth = await the.ANodeStarts();

        await the.NodeStops(third);

        await the.AgentsSettleAs(new Placement { [second] = 4, [_first] = 4, [fourth] = 4 });
    }

    [BobcatSpec]
    public async Task eject_a_stale_node()
    {
        await the.NodeBecomesTheLeader(_first);

        var second = await the.ANodeStarts();
        var third = await the.ANodeStarts();
        var fourth = await the.ANodeStarts();

        // This is just to eliminate some errors in test output
        await the.AgentsSettleAs(new Placement { [_first] = 3, [second] = 3, [third] = 3, [fourth] = 3 });

        await the.NodeGoesStale(fourth);

        await the.NodeChecksAgentHealth(_first);

        await the.AgentsSettleAs(new Placement { [_first] = 4, [second] = 4, [third] = 4 });
    }

    [BobcatSpec]
    public async Task take_over_leader_ship_if_leader_becomes_stale()
    {
        await the.NodeBecomesTheLeader(_first);

        // All remaining nodes use a 10-minute heartbeat interval so no
        // background loop races for the leadership lock after the first node
        // is disabled. The sole trigger is the CheckAgentHealth message
        // sent explicitly to the second node below.
        var second = await the.ANodeStartsWithASlowHeartbeat();
        var third = await the.ANodeStartsWithASlowHeartbeat();
        var fourth = await the.ANodeStartsWithASlowHeartbeat();

        // This is just to eliminate some errors in test output
        await the.AgentsSettleAs(new Placement { [_first] = 3, [second] = 3, [third] = 3, [fourth] = 3 });

        await the.NodeGoesStale(_first);

        // No background heartbeats can interfere - the triggered
        // CheckAgentHealth is the only path acquiring the lock.
        await the.NodeChecksAgentHealth(second);
        await the.NodeBecomesTheLeader(second);

        await the.AgentsSettleAs(new Placement { [second] = 4, [third] = 4, [fourth] = 4 });
    }

    /// <summary>
    ///     The leader dies hard -- its advisory lock goes with its connection, but its node row heartbeated a
    ///     moment ago and its assignment rows, the leader's among them, are still in the table. The survivor
    ///     that wins the lock inside StaleNodeTimeout takes over from a snapshot that still names the dead node
    ///     as leader, and must come out of that tick with exactly one persisted leader row: its own. It used to
    ///     come out with none: the takeover left the leader assignment on both nodes of its snapshot, the
    ///     GH-2602 duplicate healer stopped the copy on whichever node the store enumerated first, and when
    ///     that was the survivor the local stop deleted the row the takeover had just written.
    ///
    ///     <para>Which node a store enumerates first is its own business (a heap order, here), so this test
    ///     cannot force the losing order; <c>leader_takeover_keeps_its_own_leader_row</c> in CoreTests pins the
    ///     takeover tick in both orders. This one runs the real takeover against the real store and the real
    ///     owner-scoped delete, with a predecessor that is dead but not yet stale -- the existing takeover
    ///     tests only ever hand the successor a predecessor that is already past StaleNodeTimeout.</para>
    /// </summary>
    [BobcatSpec]
    public async Task a_leader_that_dies_inside_the_stale_window_leaves_its_successor_with_a_leader_row()
    {
        await the.NodeBecomesTheLeader(_first);

        var second = await the.ANodeStartsWithASlowHeartbeat();

        await the.AgentsSettleAs(new Placement { [_first] = 6, [second] = 6 });

        // The crash: lock released, heartbeat fresh, assignment rows untouched
        await the.NodeCrashesInsideTheStaleWindow(_first);

        // The second node polls every ten minutes, so this is the one takeover tick -- and the tracked session
        // runs the commands it emits, which is where the leader's own stop used to execute
        await the.NodeChecksAgentHealth(second);
        await the.NodeBecomesTheLeader(second);

        await the.NodeIsTheOnlyPersistedLeader(second);
    }

    [BobcatSpec]
    public async Task take_over_leader_ship_if_leader_becomes_stale_with_racing_nodes()
    {
        // Realistic scenario: the leader is disabled and the remaining
        // nodes compete for leadership through their natural 1-second
        // heartbeat loops. No explicit CheckAgentHealth messages.
        // Any node may win; we just verify one does and the cluster
        // rebalances correctly.
        await the.NodeBecomesTheLeader(_first);

        var second = await the.ANodeStarts();
        var third = await the.ANodeStarts();
        var fourth = await the.ANodeStarts();

        // This is just to eliminate some errors in test output
        await the.AgentsSettleAs(new Placement { [_first] = 3, [second] = 3, [third] = 3, [fourth] = 3 });

        await the.NodeGoesStale(_first);

        // With all nodes on a 1s interval, at least one should grab the lock before the timeout
        await the.AnotherNodeBecomesTheLeader(_first);

        await the.AgentsSettleAs(new Placement { [second] = 4, [third] = 4, [fourth] = 4 });
    }

    [BobcatSpec]
    public async Task persist_and_load_node_records()
    {
        await the.NodeLogsRecords(_first);

        await the.NodeReadsBackRecords(_first);
    }

    [BobcatSpec]
    public async Task fan_out_message_to_all_nodes()
    {
        FanOutHandler.Handled.Clear();

        await the.NodeBecomesTheLeader(_first);

        var second = await the.ANodeStarts();
        var third = await the.ANodeStarts();

        await the.AgentsSettleAs(new Placement { [_first] = 4, [second] = 4, [third] = 4 });

        await the.NodeFansOut(_first);

        await the.EveryNodeHandlesTheFannedOutMessage();
    }

    [BobcatSpec]
    public async Task broadcast_to_all_nodes_is_handled_exactly_once_on_every_node()
    {
        await the.NodeBecomesTheLeader(_first);

        var second = await the.ANodeStarts();
        var third = await the.ANodeStarts();

        await the.AgentsSettleAs(new Placement { [_first] = 4, [second] = 4, [third] = 4 });

        var ping = new BroadcastPing(Guid.NewGuid());
        await the.NodeBroadcasts(_first, ping);

        await the.EveryNodeHandlesThePingExactlyOnce(ping);
    }

    [BobcatSpec]
    public async Task ability_to_send_messages_to_correct_node_or_forward()
    {
        DoOnAgentHandler.Handled.Clear();

        await the.NodeBecomesTheLeader(_first);

        var second = await the.ANodeStarts();
        var third = await the.ANodeStarts();
        var fourth = await the.ANodeStarts();

        // This is just to eliminate some errors in test output
        await the.AgentsSettleAs(new Placement { [_first] = 3, [second] = 3, [third] = 3, [fourth] = 3 });

        await the.NodeActsOnEveryAgent(_first);

        await the.EachAgentsCommandWasHandledWhereItRuns();
    }
}

public record DoOnAgent(Uri AgentUri);

public record AgentHandled(Uri AgentUri, int NodeNumber);

public static class DoOnAgentHandler
{
    public static ConcurrentBag<AgentHandled> Handled = new();

    public static async Task HandleAsync(DoOnAgent command, IMessageContext context, CancellationToken cancellationToken)
    {
        await context.InvokeOnAgentOrForwardAsync(command.AgentUri, (runtime, c) =>
        {
            runtime.Agents.AllRunningAgentUris().ShouldContain(command.AgentUri);
            Handled.Add(new AgentHandled(command.AgentUri, runtime.DurabilitySettings.AssignedNodeNumber));

            return Task.CompletedTask;
        }, cancellationToken);
    }
}

public record TriggerFanOut;

public record FannedOut;

public static class TriggerFanOutHandler
{
    public static async Task HandleAsync(TriggerFanOut command, IMessageContext context)
    {
        await context.FanOutToAllNodes(new FannedOut());
    }
}

public static class FanOutHandler
{
    public static ConcurrentBag<int> Handled = new();

    public static void Handle(FannedOut message, IWolverineRuntime runtime)
    {
        Handled.Add(runtime.DurabilitySettings.AssignedNodeNumber);
    }
}

public record BroadcastPing(Guid Id);

public record BroadcastPingHandled(Guid Id, int NodeNumber);

public static class BroadcastPingHandler
{
    public static ConcurrentBag<BroadcastPingHandled> Handled = new();

    public static void Handle(BroadcastPing message, IWolverineRuntime runtime)
    {
        Handled.Add(new BroadcastPingHandled(message.Id, runtime.DurabilitySettings.AssignedNodeNumber));
    }
}