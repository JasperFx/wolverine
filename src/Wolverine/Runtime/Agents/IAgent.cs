using System;
using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Runtime.Agents;

#region sample_iagent
#region sample_iagent
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
    ///     Human-readable description of what this agent does on a given
    ///     node. Surfaced in monitoring tools (e.g. CritterWatch) so
    ///     operators don't have to recognise an agent purely by its URI
    ///     scheme. The default implementation returns a generic
    ///     "{scheme} agent: {Uri}" string; override in concrete agent
    ///     types to provide more specific text. Kept as a default
    ///     interface member so existing <see cref="IAgent"/>
    ///     implementations stay source-compatible.
    /// </summary>
    string Description => $"{Uri.Scheme} agent: {Uri}";

    /// <summary>
    ///     Default health check implementation based on agent status.
    ///     Override in implementations for more specific health reporting.
    /// </summary>
    Task<HealthCheckResult> IHealthCheck.CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
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

    /// <summary>
    /// The agents that this composite delegates to. Exposed read-only so diagnostics
    /// and tests can inspect the underlying agents without reflection.
    /// </summary>
    public IReadOnlyList<IAgent> InnerAgents => _agents;

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