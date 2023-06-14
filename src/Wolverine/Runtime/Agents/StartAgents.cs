using System.Runtime.CompilerServices;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record AgentsStarted(Uri[] AgentUris);

internal record AssignAgents(Guid NodeId, Uri[] AgentIds) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startAgents = new StartAgents(AgentIds);
        var response = await runtime.Agents.InvokeAsync<AgentsStarted>(NodeId, startAgents);

        runtime.Logger.LogInformation("Successfully started agents {Agents} on node {NodeId}", AgentIds, NodeId);

        foreach (var uri in response.AgentUris) runtime.Tracker.Publish(new AgentStarted(NodeId, uri));

        yield break;
    }
}

internal record StopRemoteAgents(Guid NodeId, Uri[] AgentIds) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startAgents = new StopAgents(AgentIds);
        var response = await runtime.Agents.InvokeAsync<AgentsStopped>(NodeId, startAgents);

        foreach (var uri in response.AgentUris) runtime.Tracker.Publish(new AgentStopped(uri));

        yield break;
    }
}

internal record StartAgents(Uri[] AgentUris) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var successful = new List<Uri>();
        foreach (var agentUri in AgentUris)
        {
            try
            {
                await runtime.Agents.StartLocallyAsync(agentUri);
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
}

internal record AgentsStopped(Uri[] AgentUris);

internal record StopAgents(Uri[] AgentUris) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var successful = new List<Uri>();
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
}