namespace Wolverine.Runtime.Agents;

public record NodeAgentState(IReadOnlyList<WolverineNode> Nodes, AgentRestrictions Restrictions);

/// <summary>
///     Persistence provider for Wolverine node and agent assignment information
/// </summary>
public interface INodeAgentPersistence
{
    Task ClearAllAsync(CancellationToken cancellationToken);

    Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken);
    Task DeleteAsync(Guid nodeId, int assignedNodeNumber);
    
    Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken);

    Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions,
        CancellationToken cancellationToken);
    
    Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken);

    Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken);

    Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken);
    Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken);

    Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Refresh this node's heartbeat timestamp. Returns <c>true</c> when an existing row for the node was
    /// found and updated; returns <c>false</c> when there is no row for this node — in which case the store
    /// MUST NOT insert one. A miss means a peer deleted this still-live node's row out from under it (an
    /// ejection under churn, GH-3604 / D2). The caller re-registers via <see cref="ReregisterNodeAsync"/>
    /// with the node's real identity, rather than each store blindly inserting a skeleton row with a fresh
    /// node number and no capabilities — which used to drop the live node out of capability-matched
    /// distribution and make the assignment grid re-issue its whole agent universe every cycle forever.
    /// </summary>
    Task<bool> MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken);

    /// <summary>
    /// Re-persist a node row that was deleted out from under a still-live node. Unlike
    /// <see cref="PersistAsync" />, which allocates a fresh node number, this MUST preserve the node's
    /// existing <see cref="WolverineNode.AssignedNodeNumber" /> and <see cref="WolverineNode.Capabilities" />
    /// so the resurrected row matches the identity the process is still using in memory (envelope ownership,
    /// capability-matched distribution). It is an upsert on the node id. The caller separately restores the
    /// node's agent assignments.
    /// </summary>
    Task ReregisterNodeAsync(WolverineNode node, CancellationToken cancellationToken);

    Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime);

    Task LogRecordsAsync(params NodeRecord[] records);

    Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count);

    /// <summary>
    /// Delete old node records, retaining only the most recent <paramref name="retainCount"/> records.
    /// </summary>
    Task DeleteOldNodeRecordsAsync(int retainCount) => Task.CompletedTask;
    
    
    bool HasLeadershipLock();

    Task<bool> TryAttainLeadershipLockAsync(CancellationToken token);

    Task ReleaseLeadershipLockAsync();
    
}