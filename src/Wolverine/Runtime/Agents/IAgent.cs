using System;
using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Runtime.Agents;

#region sample_IAgent

#region sample_IAgent

/// <summary>
///     Models a constantly running background process within a Wolverine
///     node cluster
/// </summary>
public interface IAgent : IHostedService, IHealthCheck
{
    /// <summary>
    ///     Unique identification for this agent within the Wolverine system
    /// </summary>
    Uri Uri { get; }

    /// <summary>
    ///     Current status of this agent
    /// </summary>
    AgentStatus Status { get; }

    /// <summary>
    ///     Default health check implementation based on agent status.
    ///     Override in implementations for more specific health reporting.
    /// </summary>
    Task<HealthCheckResult> IHealthCheck.CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Status == AgentStatus.Running
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Agent {Uri} is {Status}"));
    }
}

#endregion

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

        Status = AgentStatus.Running;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents)
        {
            await agent.StopAsync(cancellationToken);
        }

        Status = AgentStatus.Running ;
    }

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;
}

#endregion