using System.Runtime.CompilerServices;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController : IInternalHandler<VerifyAssignments>
{
    public async IAsyncEnumerable<object> HandleAsync(VerifyAssignments message)
    {
        if (LastAssignments == null)
        {
            yield break;
        }

        var dict = new Dictionary<Uri, Guid>();

        var requests = _tracker.OtherNodes()
            .Select(node => _runtime.Agents.InvokeAsync<RunningAgents>(node.Id, new QueryAgents())).ToArray();

        // Loop and find. 
        foreach (var request in requests)
        {
            var result = await request;
            foreach (var agentUri in result.Agents) dict[agentUri] = result.NodeId;
        }

        var delta = LastAssignments.FindDelta(dict);

        foreach (var command in delta) yield return command;

        _lastAssignmentCheck = DateTimeOffset.UtcNow;
    }
}

internal class QueryAgents : IAgentCommand, ISerializable
{
    
#pragma warning disable CS1998
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var agents = runtime.Agents.AllRunningAgentUris();
        AgentCommands commands = [new RunningAgents(runtime.Options.UniqueNodeId, agents)];
        return Task.FromResult(commands) ;
    }

    public byte[] Write()
    {
        return Array.Empty<byte>();
    }

    public static object Read(byte[] bytes)
    {
        return new QueryAgents();
    }
}

internal record RunningAgents(Guid NodeId, Uri[] Agents) : IAgentCommand
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentCommands.Empty);
    }
}