namespace Wolverine.Runtime.Agents;

/// <summary>
///     Persistence provider for Wolverine node and agent assignment information
/// </summary>
public interface INodeAgentPersistence
{
    Task ClearAllAsync(CancellationToken cancellationToken);

    Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken);
    Task DeleteAsync(Guid nodeId, int assignedNodeNumber);
    Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken);

    Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken);

    Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken);
    Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken);

    [Obsolete("Kill this in 3.0")]
    Task<Guid?> MarkNodeAsLeaderAsync(Guid? originalLeader, Guid id);
    Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken);
    Task MarkHealthCheckAsync(Guid nodeId);
    
    Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime);
    
    [Obsolete("kill this in 3.0")]
    Task<IReadOnlyList<int>> LoadAllNodeAssignedIdsAsync();


    Task LogRecordsAsync(params NodeRecord[] records);

    Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count);
    
    
    bool HasLeadershipLock();

    Task<bool> TryAttainLeadershipLockAsync(CancellationToken token);

    Task ReleaseLeadershipLockAsync();
}