using JasperFx;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Runtime.Agents;

/// <summary>
/// Base class for a Wolverine agent that should run on only one
/// node within your system
/// </summary>
public abstract class SingularAgent : IAgent, IAgentFamily
{
    /// <summary>
    /// Base constructor for SingularAgent classes
    /// </summary>
    /// <param name="scheme">Descriptive name for Wolverine. Will be the scheme or protocol name for the Agent Uri</param>
    public SingularAgent(string scheme)
    {
        Scheme = scheme;
        Uri = new Uri($"{Scheme}://");
    }

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        await startAsync(cancellationToken);
        Status = AgentStatus.Running;
    }

    /// <summary>
    /// Start processing within your system
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected abstract Task startAsync(CancellationToken cancellationToken);

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        await stopAsync(cancellationToken);
        Status = AgentStatus.Stopped;
    }

    /// <summary>
    /// Stop processing within your system
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected abstract Task stopAsync(CancellationToken cancellationToken);

    public Uri Uri { get; }
    public AgentStatus Status { get; protected set; } = AgentStatus.Stopped;


    public string Scheme { get; }

    ValueTask<IReadOnlyList<Uri>> IAgentFamily.AllKnownAgentsAsync()
    {
        return new ValueTask<IReadOnlyList<Uri>>([Uri]);
    }

    ValueTask<IAgent> IAgentFamily.BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        return new ValueTask<IAgent>(this);
    }

    ValueTask<IReadOnlyList<Uri>> IAgentFamily.SupportedAgentsAsync()
    {
        return new ValueTask<IReadOnlyList<Uri>>([Uri]);
    }

    ValueTask IAgentFamily.EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        var agent = assignments.AgentFor(Uri);
        if (agent.IsPaused) return new ValueTask();

        if (agent.AssignedNode != null) return new ValueTask();

        var node = assignments.Nodes.FirstOrDefault(x => !x.IsLeader) ?? assignments.Nodes.FirstOrDefault();
        node?.Assign(agent);

        return new ValueTask();
    }
}