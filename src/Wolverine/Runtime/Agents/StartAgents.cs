using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record AgentsStarted(Uri[] AgentUris);

internal record AssignAgents(Guid NodeId, Uri[] AgentIds) : IAgentCommand, ISerializable
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startAgents = new StartAgents(AgentIds);
        var response = await runtime.Agents.InvokeAsync<AgentsStarted>(NodeId, startAgents);

        runtime.Logger.LogInformation("Successfully started agents {Agents} on node {NodeNumber}", AgentIds, runtime.Options.Durability.AssignedNodeNumber);

        foreach (var uri in response.AgentUris) runtime.Tracker.Publish(new AgentStarted(NodeId, uri));

        yield break;
    }

    public byte[] Write()
    {
        return NodeId.ToByteArray().Concat(Encoding.UTF8.GetBytes(AgentIds.Select(x => x.ToString()).Join(","))).ToArray();
    }

    public static object Read(byte[] bytes)
    {
        var uris = Encoding.UTF8.GetString(bytes.Skip(16).ToArray()).Split(',').Select(x => new Uri(x))
            .ToArray();
        return new AssignAgents(new Guid(bytes.Take(16).ToArray()), uris);
    }
}

internal record StopRemoteAgents(Guid NodeId, Uri[] AgentIds) : IAgentCommand, ISerializable
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startAgents = new StopAgents(AgentIds);
        var response = await runtime.Agents.InvokeAsync<AgentsStopped>(NodeId, startAgents);

        foreach (var uri in response.AgentUris) runtime.Tracker.Publish(new AgentStopped(uri));

        yield break;
    }
    
    public byte[] Write()
    {
        return NodeId.ToByteArray().Concat(Encoding.UTF8.GetBytes(AgentIds.Select(x => x.ToString()).Join(","))).ToArray();
    }

    public static object Read(byte[] bytes)
    {
        var uris = Encoding.UTF8.GetString(bytes.Skip(16).ToArray()).Split(',').Select(x => new Uri(x))
            .ToArray();
        return new StopRemoteAgents(new Guid(bytes.Take(16).ToArray()), uris);
    }
}

internal record StartAgents(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var successful = new List<Uri>(AgentUris.Length);
        foreach (var agentUri in AgentUris)
        {
            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                
                await runtime.Agents.StartLocallyAsync(agentUri);

                Debug.WriteLine($"STARTED {agentUri} in {stopwatch.ElapsedMilliseconds} MILLISECONDS");
                stopwatch.Stop();
                
                successful.Add(agentUri);
            }
            catch (Exception e)
            {
                runtime.Logger.LogError(e, "Failed to start requested agent {AgentUri}", agentUri);
            }
        }

        yield return new AgentsStarted(successful.ToArray());
    }

    public virtual bool Equals(StartAgents? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return AgentUris.SequenceEqual(other.AgentUris);
    }

    public override int GetHashCode()
    {
        return AgentUris.GetHashCode();
    }

    public override string ToString()
    {
        return $"Start agents {AgentUris.Select(x => x.ToString()).Join(", ")}";
    }
    
    public byte[] Write()
    {
        return Encoding.UTF8.GetBytes(AgentUris.Select(x => x.ToString()).Join(","));
    }

    public static object Read(byte[] bytes)
    {
        var agents = Encoding.UTF8.GetString(bytes).Split(',')
            .Select(x => new Uri(x)).ToArray();
        return new StartAgents(agents);
    }
}

internal record AgentsStopped(Uri[] AgentUris);

internal record StopAgents(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var successful = new List<Uri>(AgentUris.Length);
        foreach (var agentUri in AgentUris)
        {
            try
            {
                await runtime.Agents.StopLocallyAsync(agentUri);
                successful.Add(agentUri);
            }
            catch (Exception e)
            {
                runtime.Logger.LogError(e, "Failed to start requested agent {AgentUri}", agentUri);
            }
        }

        yield return new AgentsStopped(successful.ToArray());
    }

    public virtual bool Equals(StopAgents? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return AgentUris.SequenceEqual(other.AgentUris);
    }

    public override int GetHashCode()
    {
        return AgentUris.GetHashCode();
    }

    public override string ToString()
    {
        return $"Stop agents {AgentUris.Select(x => x.ToString()).Join(", ")}";
    }

    public byte[] Write()
    {
        return Encoding.UTF8.GetBytes(AgentUris.Select(x => x.ToString()).Join(","));
    }

    public static object Read(byte[] bytes)
    {
        var agents = Encoding.UTF8.GetString(bytes).Split(',')
            .Select(x => new Uri(x)).ToArray();
        return new StopAgents(agents);
    }
}