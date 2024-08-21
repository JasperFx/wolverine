using JasperFx.Core;
using Raven.Client.Documents;
using Wolverine.Runtime.Agents;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : INodeAgentPersistence
{
    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        // Shouldn't really get called at runtime, so we're doing it crudely
        using var session = _store.OpenAsyncSession();
        var nodes = await session.Query<WolverineNode>().ToListAsync(cancellationToken);
        foreach (var node in nodes)
        {
            session.Delete(node);
        }
        
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(node, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        
        // TODO -- how to effect a sequence
        return 1;
    }

    public async Task DeleteAsync(Guid nodeId)
    {
        using var session = _store.OpenAsyncSession();
        session.Delete(nodeId.ToString());
        await session.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        using var session = _store.OpenSession();
        var answer = await session.Query<WolverineNode>().ToListAsync(token: cancellationToken);
        return answer;
    }

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var node = await session.LoadAsync<WolverineNode>(nodeId.ToString(), token: cancellationToken);
        node ??= new WolverineNode
        {
            NodeId = nodeId
        };

        node.AssignAgents(agents);
        
        await session.SaveChangesAsync(token: cancellationToken);
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var node = await session.LoadAsync<WolverineNode>(nodeId.ToString(), token: cancellationToken);
        node ??= new WolverineNode
        {
            NodeId = nodeId
        };
        
        node.ActiveAgents.Remove(agentUri);
        
        await session.SaveChangesAsync(token: cancellationToken);
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var node = await session.LoadAsync<WolverineNode>(nodeId.ToString(), token: cancellationToken);
        node ??= new WolverineNode
        {
            NodeId = nodeId
        };
        
        node.ActiveAgents.Fill(agentUri);
        
        await session.SaveChangesAsync(token: cancellationToken);
    }

    public async Task<Guid?> MarkNodeAsLeaderAsync(Guid? originalLeader, Guid id)
    {
        throw new NotImplementedException();
    }

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<WolverineNode>(nodeId.ToString(), token: cancellationToken);
    }

    public async Task MarkHealthCheckAsync(Guid nodeId)
    {
        using var session = _store.OpenAsyncSession();
        var node = await session.LoadAsync<WolverineNode>(nodeId.ToString());
        node.LastHealthCheck = DateTimeOffset.UtcNow;
        await session.StoreAsync(node);
        await session.SaveChangesAsync();
    }

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        using var session = _store.OpenAsyncSession();
        var node = await session.LoadAsync<WolverineNode>(nodeId.ToString());
        node.LastHealthCheck = lastHeartbeatTime;
        await session.StoreAsync(node);
        await session.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<int>> LoadAllNodeAssignedIdsAsync()
    {
        using var session = _store.OpenAsyncSession();
        var ids = await session.Query<WolverineNode>().Select(x => x.AssignedNodeId).ToListAsync();
        return ids;
    }

    public async Task LogRecordsAsync(params NodeRecord[] records)
    {
        using var session = _store.OpenAsyncSession();
        foreach (var record in records)
        {
            await session.StoreAsync(record);
        }
        
        await session.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count)
    {
        using var session = _store.OpenAsyncSession();
        var list = await session.Query<NodeRecord>().OrderByDescending(x => x.Timestamp).Take(count).ToListAsync();
        list.Reverse();
        return list;
    }

    public bool HasLeadershipLock()
    {
        throw new NotImplementedException();
    }

    public async Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public async Task ReleaseLeadershipLockAsync()
    {
        throw new NotImplementedException();
    }
}
