using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Wolverine.Runtime.Agents;
using ISubscriptionAgent = JasperFx.Events.Daemon.ISubscriptionAgent;

namespace Wolverine.Marten.Distribution;

internal class EventSubscriptionAgent : IAgent
{
    private readonly ShardName _shardName;
    private readonly IProjectionDaemon _daemon;
    private ISubscriptionAgent? _innerAgent;

    public EventSubscriptionAgent(Uri uri, ShardName shardName, IProjectionDaemon daemon)
    {
        _shardName = shardName;
        _daemon = daemon;
        Uri = uri;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _innerAgent = await _daemon.StartAgentAsync(_shardName, cancellationToken);
        Status = AgentStatus.Running;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _daemon.StopAgentAsync(_shardName);
        Status = AgentStatus.Stopped;
    }

    public Uri Uri { get; }
    
    // Be nice for this to get the Paused too
    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;
}