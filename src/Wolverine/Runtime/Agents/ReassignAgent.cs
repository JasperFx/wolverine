using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record ReassignAgent(Uri AgentUri, NodeDestination OriginalNode, NodeDestination ActiveNode) : IAgentCommand
{
    // GH-3749: keyed to the SOURCE node. The only blocking work this command does inline is the stop round
    // trip against OriginalNode, and the lanes exist precisely so that a node slow (or dead) on the wire
    // wedges nothing but its own queue -- keyed to ActiveNode, a source mid-replay froze every batched
    // AssignAgents chunk queued behind it for a perfectly healthy destination. The start half is cascaded
    // as an AssignAgent, which the dispatcher routes into ActiveNode's own lane, so it still queues behind
    // whatever work the destination already has. That ordering also covers the GH-3698 re-target of a
    // dispatched-but-unconfirmed start: the stop runs in the lane where that start may still be queued,
    // behind it, instead of racing ahead and finding nothing to stop.
    public Guid? DestinationNodeId => OriginalNode.NodeId;

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.Agents.InvokeAsync(OriginalNode, new StopAgent(AgentUri));
        }
        catch (UnknownWolverineNodeException e)
        {
            runtime.Logger.LogWarning(e,
                "Error trying to reassign a running agent {AgentUri} from {CurrentNodeId} to {NewNodeId}", AgentUri,
                OriginalNode, ActiveNode);
            return AgentCommands.Empty;
        }

        runtime.Logger.LogInformation("Successfully stopped agent {Agent} on node {OriginalNode}", AgentUri,
            OriginalNode.NodeId);

        // Do this in separate steps
        return [new AssignAgent(AgentUri, ActiveNode)];
    }
}

/// <summary>
///     GH-3749: the batched form of <see cref="ReassignAgent" />, produced by
///     <c>NodeAgentController.batchCommands</c> for a group of agents all moving between the same two nodes,
///     chunked by <c>AgentStartBatchSize</c>. Reassignment was the one command type never batched: a
///     rebalance emitted one <see cref="ReassignAgent" /> per moved agent, each costing its own serial
///     <see cref="StopAgent" /> round trip against the source inside one lane, and
///     <c>AgentStartBatchSize</c> had no effect on any of them.
/// </summary>
internal record ReassignAgents(NodeDestination OriginalNode, NodeDestination ActiveNode, Uri[] AgentUris)
    : IAgentCommand
{
    // Source lane, for the same reason as ReassignAgent: the inline round trip is the stop against
    // OriginalNode, and the cascaded starts land in ActiveNode's lane on their own.
    public Guid? DestinationNodeId => OriginalNode.NodeId;

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var timeout = AgentBatchTimeouts.ReplyWindowFor(AgentUris.Length);
        var response =
            await runtime.Agents.InvokeAsync<AgentsStopped>(OriginalNode, new StopAgents(AgentUris), timeout);

        if (response == null)
        {
            return AgentCommands.Empty;
        }

        var confirmed = response.AgentUris ?? [];
        var unconfirmed = AgentUriSet.Missing(AgentUris, confirmed);

        if (unconfirmed.Length > 0)
        {
            // Only a CONFIRMED stop may be followed by a start elsewhere -- starting an agent whose old
            // copy may still be running is the two-copies bug the GH-3698 wave stamped out. The stragglers
            // stay where they are and the leader re-decides them on a later evaluation.
            runtime.Logger.LogWarning(
                "Node {OriginalNode} confirmed stopping {ConfirmedCount} of {RequestedCount} agents being reassigned to node {ActiveNode}; {UnconfirmedCount} remain unconfirmed and will be re-evaluated: {UnconfirmedAgents}",
                OriginalNode.NodeId, confirmed.Length, AgentUris.Length, ActiveNode.NodeId, unconfirmed.Length,
                unconfirmed);
        }

        if (confirmed.Length == 0)
        {
            return AgentCommands.Empty;
        }

        return [new AssignAgents(ActiveNode, confirmed)];
    }

    // See AgentUriSet: value equality over the agents, independent of their order, so the dispatcher can
    // recognise the same reassignment work re-emitted by a later evaluation.
    public virtual bool Equals(ReassignAgents? other)
        => other is not null && OriginalNode == other.OriginalNode && ActiveNode == other.ActiveNode
                             && AgentUriSet.AreEquivalent(AgentUris, other.AgentUris);

    public override int GetHashCode() => HashCode.Combine(OriginalNode, ActiveNode, AgentUriSet.HashOf(AgentUris));
}

/// <summary>
///     GH-3748: the request/reply window for every batched agent command, scaled to the batch size.
///
///     <para>The old shape gave a chunk <c>30s + 1s per agent</c> (starts) or a flat default (stops), which
///     assumes an agent starts or stops in about a second. A Marten projection shard behind a version bump
///     replays synchronously inside its start (jasperfx#480's side-effect gate) and was measured at 27s p50 /
///     82s p95 / 215s max per shard — so no achievable batch size satisfied the window and every chunk timed
///     out (15 of 17 at <c>AgentStartBatchSize = 100</c>). Past <see cref="SlowWorkThreshold" /> agents the
///     per-agent allowance is 30 seconds instead.</para>
///
///     <para>The window is a backstop, not a pace-setter: the reply arrives as soon as the batch has actually
///     been worked through, and a partial confirmation (GH-3750) reports whatever made it. All the window
///     decides is when to give up and let the leader re-decide, so a generous window costs little and a tight
///     one is a cluster that can never converge.</para>
/// </summary>
internal static class AgentBatchTimeouts
{
    internal const int SlowWorkThreshold = 20;

    public static TimeSpan ReplyWindowFor(int agentCount)
    {
        var perAgentSeconds = agentCount > SlowWorkThreshold ? 30 : 1;
        return 30.Seconds() + (agentCount * perAgentSeconds).Seconds();
    }
}
