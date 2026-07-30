using System.Text;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record ReassignAgent(Uri AgentUri, NodeDestination OriginalNode, NodeDestination ActiveNode) : IAgentCommand
{
    /// <summary>
    ///     GH-3698. Run the stop half in <see cref="OriginalNode" />'s lane rather than
    ///     <see cref="ActiveNode" />'s. Set when the agent is being taken off a node that may not have
    ///     <i>started</i> it yet — a dispatch the leader has sent but not seen confirmed. The stop then queues
    ///     behind that still-pending start instead of racing ahead of it and finding nothing to stop, which
    ///     would leave the agent coming up on both nodes.
    /// </summary>
    internal bool StopInSourceLane { get; init; }

    // Two nodes are involved, but only the stop half runs inline here -- the start is cascaded back to the
    // drain as a separate AssignAgent, which lands in ActiveNode's lane on the next round. Keying the whole
    // command to ActiveNode keeps a reassignment behind any other work already queued for the node that is
    // about to take the agent; keying it to OriginalNode instead orders it behind that node's own queue,
    // which is what matters when the agent might still be starting there.
    public Guid? DestinationNodeId => StopInSourceLane ? OriginalNode.NodeId : ActiveNode.NodeId;

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