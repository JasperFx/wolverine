using System.Text;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record AssignAgent(Uri AgentUri, NodeDestination Destination) : IAgentCommand
{
    public Guid? DestinationNodeId => Destination.NodeId;

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        if (Destination.NodeId == runtime.Options.UniqueNodeId)
        {
            await runtime.Agents.StartLocallyAsync(AgentUri);

            if (runtime is WolverineRuntime localRuntime)
            {
                localRuntime.NodeController?.ConfirmDispatched([AgentUri], Destination.NodeId);
            }
        }
        else
        {
            // GH-3748: the acknowledgement only arrives when the start has actually finished, and a
            // single slow start (a Marten shard replaying behind a version bump was measured at up to
            // 215s) can never beat the fixed acknowledgement window. Racing it against presence polling
            // lets a slow-but-happening start be confirmed by observation instead of failed by the clock.
            var confirmed = await AgentWorkConfirmation.AwaitAsync(runtime, Destination, [AgentUri],
                startRemotelyAsync(runtime), AgentPresenceDirection.Running, cancellationToken);

            if (confirmed.Length == 0)
            {
                runtime.Logger.LogWarning(
                    "Unable to confirm that agent {AgentUri} started on node {NodeId}; the assignment will be re-evaluated",
                    AgentUri, Destination.NodeId);
                return AgentCommands.Empty;
            }

            // GH-3750: see AssignAgents — hold the confirmed placement in the pending ledger until the
            // persisted assignment row is visible to an evaluation snapshot.
            if (runtime is WolverineRuntime wolverineRuntime)
            {
                wolverineRuntime.NodeController?.ConfirmDispatched([AgentUri], Destination.NodeId);
            }
        }

        runtime.Logger.LogInformation("Successfully started agent {AgentUri} on node {NodeId}", AgentUri, runtime.Options.Durability.AssignedNodeNumber);

        return AgentCommands.Empty;
    }

    private async Task<Uri[]> startRemotelyAsync(IWolverineRuntime runtime)
    {
        try
        {
            await runtime.Agents.InvokeAsync(Destination, new StartAgent(AgentUri));
        }
        catch (UnknownWolverineNodeException e)
        {
            runtime.Logger.LogWarning(e, "Error while trying to assign agent {AgentUri} to {NodeId}", AgentUri,
                Destination.NodeId);
            return [];
        }

        return [AgentUri];
    }

    public virtual bool Equals(AssignAgent? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return AgentUri.Equals(other.AgentUri) && Destination.NodeId.Equals(other.Destination.NodeId);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(AgentUri, Destination.NodeId);
    }
}