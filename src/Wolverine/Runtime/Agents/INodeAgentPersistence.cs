namespace Wolverine.Runtime.Agents;

/// <summary>
///     Persistence provider for Wolverine node and agent assignment information
/// </summary>
public interface INodeAgentPersistence
{
    Task ClearAllAsync(CancellationToken cancellationToken);

    Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken);
    Task DeleteAsync(Guid nodeId);
    Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken);

    Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken);

    Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken);
    Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken);

    Task<Guid?> MarkNodeAsLeaderAsync(Guid? originalLeader, Guid id);
    Task<Uri?> FindLeaderControlUriAsync(Guid selfId);
    Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken);
    Task MarkHealthCheckAsync(Guid nodeId);
    Task<IReadOnlyList<Uri>> LoadAllOtherNodeControlUrisAsync(Guid selfId);

    [Obsolete("Will be removed in Wolverine 3.0")]
    Task<IReadOnlyList<WolverineNode>> LoadAllStaleNodesAsync(DateTimeOffset staleTime, CancellationToken cancellation);
    Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime);
    Task<IReadOnlyList<int>> LoadAllNodeAssignedIdsAsync();


    Task LogRecordsAsync(params NodeRecord[] records);

    Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count);
}