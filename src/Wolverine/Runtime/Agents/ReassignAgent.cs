using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record ReassignAgent(Uri AgentUri, Guid CurrentNodeId, Guid NewNodeId) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await runtime.Agents.InvokeAsync(CurrentNodeId, new StopAgent(AgentUri));
        }
        catch (UnknownWolverineNodeException e)
        {
            runtime.Logger.LogWarning(e, "Error trying to reassign a running agent {AgentUri} from {CurrentNodeId} to {NewNodeId}", AgentUri, CurrentNodeId, NewNodeId);
            yield break;
        }
        
        runtime.Tracker.Publish(new AgentStopped(AgentUri));

        // Do this in separate steps
        yield return new AssignAgent(AgentUri, NewNodeId);
    }
}