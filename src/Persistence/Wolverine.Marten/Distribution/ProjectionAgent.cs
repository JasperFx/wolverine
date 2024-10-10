using Marten.Events.Daemon.Coordination;
using Wolverine.Runtime.Agents;

namespace Wolverine.Marten.Distribution;

internal class ProjectionAgent : IAgent
{
    private readonly string _shardName;
    private readonly string _databaseName;
    private readonly IProjectionCoordinator _coordinator;

    public ProjectionAgent(Uri uri, IProjectionCoordinator coordinator)
    {
        (_databaseName, _shardName) = ProjectionAgents.Parse(uri);
        _coordinator = coordinator;

        Uri = uri;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var daemon = await _coordinator.DaemonForDatabase(_databaseName);
        await daemon.StartAgentAsync(_shardName, cancellationToken);
        Status = AgentStatus.Started;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var daemon = await _coordinator.DaemonForDatabase(_databaseName);
        await daemon.StopAgentAsync(_shardName);
        Status = AgentStatus.Stopped;
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        var daemon = await _coordinator.DaemonForDatabase(_databaseName);
        await daemon.StopAgentAsync(_shardName);
        Status = AgentStatus.Paused;
    }

    public Uri Uri { get; }
    public AgentStatus Status { get; set; } = AgentStatus.Stopped;
}