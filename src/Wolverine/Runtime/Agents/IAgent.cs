using System;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Runtime.Agents;

#region sample_IAgent

/// <summary>
///     Models a constantly running background process within a Wolverine
///     node cluster
/// </summary>
public interface IAgent : IHostedService
{
    /// <summary>
    ///     Unique identification for this agent within the Wolverine system
    /// </summary>
    Uri Uri { get; }
    
    AgentStatus Status { get; }
}

public class CompositeAgent : IAgent
{
    private readonly List<IAgent> _agents;
    public Uri Uri { get; }

    public CompositeAgent(Uri uri, IEnumerable<IAgent> agents)
    {
        Uri = uri;
        _agents = agents.ToList();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents)
        {
            await agent.StartAsync(cancellationToken);
        }

        Status = AgentStatus.Started;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents)
        {
            await agent.StopAsync(cancellationToken);
        }

        Status = AgentStatus.Started;
    }

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;
}

public enum AgentStatus
{
    Started,
    Stopped,
    Paused
}

#endregion