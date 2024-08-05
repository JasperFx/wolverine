using System.Diagnostics;
using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record AgentsStarted(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentCommands.Empty);
    }

    public byte[] Write()
    {
        return Encoding.UTF8.GetBytes(AgentUris.Select(x => x.ToString()).Join(","));
    }

    public static object Read(byte[] bytes)
    {
        var uris = Encoding.UTF8.GetString(bytes).Split(',').Select(x => new Uri(x)).ToArray();
        return new AgentsStarted(uris);
    }
}

internal record AssignAgents(NodeDestination Destination, Uri[] AgentIds) : IAgentCommand
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var startAgents = new StartAgents(AgentIds);
        var response = await runtime.Agents.InvokeAsync<AgentsStarted>(Destination, startAgents);

        if (response == null)
        {
            return AgentCommands.Empty;
        }

        runtime.Logger.LogInformation("Successfully started agents {Agents} on node {NodeNumber}", AgentIds, runtime.Options.Durability.AssignedNodeNumber);

        return AgentCommands.Empty;
    }
}

internal record StopRemoteAgents(NodeDestination Destination, Uri[] AgentIds) : IAgentCommand
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var startAgents = new StopAgents(AgentIds);
        await runtime.Agents.InvokeAsync<AgentsStopped>(Destination, startAgents);

        return AgentCommands.Empty;
    }
}

internal record StartAgents(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
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

        return [new AgentsStarted(successful.ToArray())];
    }

    public virtual bool Equals(StartAgents? other)
    {
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

internal record AgentsStopped(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentCommands.Empty);
    }

    public byte[] Write()
    {
        return Encoding.UTF8.GetBytes(AgentUris.Select(x => x.ToString()).Join(","));
    }

    public static object Read(byte[] bytes)
    {
        var uris = Encoding.UTF8.GetString(bytes).Split(',').Select(x => new Uri(x)).ToArray();
        return new AgentsStopped(uris);
    }
}

internal record StopAgents(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
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

        return [new AgentsStopped(successful.ToArray())];
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