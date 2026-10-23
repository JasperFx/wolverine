namespace Wolverine.Runtime.Agents;

/// <summary>
///     GH-5163. Why the leader left an agent without a node on an assignment evaluation. Exposed through
///     <see cref="AssignmentGrid.Agent.UnplacedReason" />, which is null for an agent that is assigned or
///     whose reason the grid could not determine.
/// </summary>
public enum UnplacedReason
{
    /// <summary>
    ///     Capacity-aware assignment is on and no serving node is accepting new agents, so the agent waits
    ///     until a node advertises headroom again. More nodes or less load is the remedy, not a restart.
    /// </summary>
    AwaitingCapacity,

    /// <summary>
    ///     No node in the cluster declares the capability to run this agent, e.g. a blue/green deployment
    ///     where only the nodes of one version know about it and none of them are running.
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
    TakeoverHold
}
