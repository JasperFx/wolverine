using System.Runtime.CompilerServices;
using System.Text;
using JasperFx.Core;
using Oakton.Internal.Conversion;

namespace Wolverine.Runtime.Agents;

public record VerifyAssignments : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return runtime.Agents.VerifyAssignmentsAsync();
    }
    
    public byte[] Write()
    {
        return Array.Empty<byte>();
    }

    public static object Read(byte[] bytes)
    {
        return new VerifyAssignments();
    }
}

public partial class NodeAgentController
{
    public async Task<AgentCommands> VerifyAssignmentsAsync()
    {
        if (LastAssignments == null)
        {
            return AgentCommands.Empty;
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
        var commands = new AgentCommands();
        commands.AddRange(delta);

        _lastAssignmentCheck = DateTimeOffset.UtcNow;

        return commands;
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

internal record RunningAgents(Guid NodeId, Uri[] Agents) : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentCommands.Empty);
    }
    
    public byte[] Write()
    {
        return NodeId.ToByteArray().Concat(Encoding.UTF8.GetBytes(Agents.Select(x => x.ToString()).Join(","))).ToArray() ;
    }

    public static object Read(byte[] bytes)
    {
        var nodeId = new Guid(bytes.Take(16).ToArray());
        var agents = Encoding.UTF8.GetString(bytes.Skip(16).ToArray()).Split(',')
            .Select(x => new Uri(x)).ToArray();
        return new RunningAgents(nodeId, agents);
    }
}