using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record ReassignAgent(Uri AgentUri, Guid OriginalNodeId, Guid ActiveNodeId) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await runtime.Agents.InvokeAsync(OriginalNodeId, new StopAgent(AgentUri));
        }
        catch (UnknownWolverineNodeException e)
        {
            runtime.Logger.LogWarning(e,
                "Error trying to reassign a running agent {AgentUri} from {CurrentNodeId} to {NewNodeId}", AgentUri,
                OriginalNodeId, ActiveNodeId);
            yield break;
        }

        runtime.Logger.LogInformation("Successfully stopped agent {Agent} on node {OriginalNode}", AgentUri,
            OriginalNodeId);
        runtime.Tracker.Publish(new AgentStopped(AgentUri));

        // Do this in separate steps
        yield return new AssignAgent(AgentUri, ActiveNodeId);
    }
}