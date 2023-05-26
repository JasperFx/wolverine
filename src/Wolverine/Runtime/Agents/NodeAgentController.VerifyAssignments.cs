using System.Runtime.CompilerServices;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController : IInternalHandler<VerifyAssignments>
{
    public async IAsyncEnumerable<object> HandleAsync(VerifyAssignments message)
    {
        if (LastAssignments == null) yield break;
        
        var dict = new Dictionary<Uri, Guid>();

        var requests = _tracker.OtherNodes()
            .Select(node => _runtime.Agents.InvokeAsync<RunningAgents>(node.Id, new QueryAgents())).ToArray();
        
        // Loop and find. 
        foreach (var request in requests)
        {
            var result = await request;
            foreach (var agentUri in result.Agents)
            {
                dict[agentUri] = result.NodeId;
            }
        }

        var delta = LastAssignments.FindDelta(dict);

        foreach (var command in delta)
        {
            yield return command;
        }
        
        _lastAssignmentCheck = DateTimeOffset.UtcNow;
    }
}

internal class QueryAgents : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agents = runtime.Agents.AllRunningAgentUris();
        yield return new RunningAgents(runtime.Options.UniqueNodeId, agents);
    }
}

internal record RunningAgents(Guid NodeId, Uri[] Agents);