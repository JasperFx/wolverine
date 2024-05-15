using System.Text;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record ReassignAgent(Uri AgentUri, Guid OriginalNodeId, Guid ActiveNodeId) : IAgentCommand, ISerializable
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
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
            return AgentCommands.Empty;
        }

        runtime.Logger.LogInformation("Successfully stopped agent {Agent} on node {OriginalNode}", AgentUri,
            OriginalNodeId);
        runtime.Tracker.Publish(new AgentStopped(AgentUri));

        // Do this in separate steps
        return [new AssignAgent(AgentUri, ActiveNodeId)];
    }

    public byte[] Write()
    {
        return OriginalNodeId.ToByteArray()
            .Concat(ActiveNodeId.ToByteArray())
            .Concat(Encoding.UTF8.GetBytes(AgentUri.ToString())).ToArray();
    }

    public static object Read(byte[] bytes)
    {
        var agentUriString = Encoding.UTF8.GetString(bytes[32..]);
        return new ReassignAgent(new Uri(agentUriString), new Guid(bytes[..16]), new Guid(bytes[16..][..16]));
    }
}