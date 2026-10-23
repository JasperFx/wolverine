namespace Wolverine.Runtime.Agents;

/// <summary>
///     GH-5163. Why the leader left an agent without a node on an assignment evaluation. Exposed through
///     <see cref="AssignmentGrid.Agent.UnplacedReason" />, which is null for an agent that is assigned or
///     whose reason the grid could not determine.
///
///     <para><b>The member names are a stable contract.</b> CritterWatch carries the reason by its NAME on the
///     wire and persists it into alert records, so a member may be added, but an existing member must never be
///     renamed or removed. Ordinal values are not used by any consumer and carry no meaning. This is pinned by
///     <c>CoreTests.Runtime.Agents.unplaced_reason_names_are_a_stable_contract</c>.</para>
/// </summary>
public enum UnplacedReason
{
    /// <summary>
    ///     Capacity-aware assignment is on and no serving node is accepting new agents, so the agent waits
    ///     until a node advertises headroom again. More nodes or less load is the remedy, not a restart.
    /// </summary>
    AwaitingCapacity,

    /// <summary>
    ///     No node in the cluster is eligible to run this agent: none declares the capability for it, e.g. a
    ///     blue/green deployment where only the nodes of one version know about it and none of them are running,
    ///     or the only nodes that could are warm-up nodes (<see cref="NodeRole.Warmup" />), which never take
    ///     work the serving nodes run.
    /// </summary>
    NoCapableNode,

    /// <summary>
    ///     The agent is paused through an operator restriction.
    /// </summary>
    Paused,

    /// <summary>
    ///     A newly elected leader is holding first-time placements back for one or more evaluations, after
    ///     taking over a running cluster or as the first node of a cold one (see
    ///     <see cref="DurabilitySettings.LeaderTakeoverHoldEvaluations" /> and
    ///     <see cref="DurabilitySettings.LeaderColdStartHoldEvaluations" />). The agent is placed on a later
    ///     evaluation.
    /// </summary>
    TakeoverHold,

    /// <summary>
    ///     The agent has been replaced and is deliberately stopped, not waiting for a node: an event subscription
    ///     agent whose database changed tenant scoping, so the same shard now runs as differently scoped agents
    ///     (a store-global agent replaced by per-tenant ones, or the reverse). It stays visible only while some
    ///     node still declares it from its startup capabilities.
    /// </summary>
    Superseded
}
