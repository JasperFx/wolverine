using JasperFx.Core;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Wolverine.Runtime.Agents;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : INodeAgentPersistence
{
    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        // Shouldn't really get called at runtime, so we're doing it crudely
        var nodes = await LoadAllNodesAsync(cancellationToken);
        using var session = _store.OpenAsyncSession();
        
        foreach (var node in nodes)
        {
            session.Delete(node);
            foreach (var agent in node.ActiveAgents)
            {
                session.Delete(AgentAssignment.ToId(agent));
            }
        }
        
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession(new SessionOptions
        {
            TransactionMode = TransactionMode.ClusterWide
        });
        
        var sequence = await session.LoadAsync<NodeSequence>(NodeSequence.SequenceId, cancellationToken);
        sequence ??= new NodeSequence();

        node.AssignedNodeNumber = ++sequence.Count;
        
        await session.StoreAsync(sequence, cancellationToken);
        await session.StoreAsync(node, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        
        return node.AssignedNodeNumber; 
    }

    public async Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        using var session = _store.OpenAsyncSession();
        session.Delete(nodeId.ToString());
        
        await session.SaveChangesAsync();
        
        // Actually okay for these to be eventually consistent

        var query = new IndexQuery
        {
            Query = $"from AgentAssignments a where a.NodeId = $node",
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 5.Seconds(),
            QueryParameters = new(){{"node", nodeId}}
        };
        
        var op = await _store.Operations.SendAsync(
            new DeleteByQueryOperation(query));
        await op.WaitForCompletionAsync();

        await ReleaseAllOwnershipAsync(assignedNodeNumber);
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var answer = await session
            .Query<WolverineNode>()
            .Customize(x => x.WaitForNonStaleResults())
            .ToListAsync(token: cancellationToken);

        var assignments = await session
            .Query<AgentAssignment>()
            .Customize(x => x.WaitForNonStaleResults())
            .ToListAsync(token: cancellationToken);
        
        foreach (var node in answer)
        {
            node.ActiveAgents.Clear();
            node.ActiveAgents.AddRange(assignments.Where(x => x.NodeId == node.NodeId).Select(x => x.AgentUri));
        }
        
        return answer;
    }

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        foreach (var agent in agents)
        {
            var agentAssignment = new AgentAssignment(agent, nodeId);
            await session.StoreAsync(agentAssignment, cancellationToken);
        }
        
        await session.SaveChangesAsync(token: cancellationToken);
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        session.Delete(AgentAssignment.ToId(agentUri));
        
        await session.SaveChangesAsync(token: cancellationToken);
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();

        var agentAssignment = new AgentAssignment(agentUri, nodeId);
        await session.StoreAsync(agentAssignment, agentAssignment.Id, cancellationToken);
        
        await session.SaveChangesAsync(token: cancellationToken);
    }

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var node = await session.LoadAsync<WolverineNode>(nodeId.ToString(), token: cancellationToken);
        var agents = await session.Query<AgentAssignment>().Customize(x => x.WaitForNonStaleResults()).Where(x => x.NodeId == nodeId).ToListAsync(token: cancellationToken);
        node.ActiveAgents.Clear();
        node.ActiveAgents.AddRange(agents.OrderBy(x => x.Id).Select(x => x.AgentUri));

        return node;
    }

    public async Task MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.AddOrPatch(node.NodeId.ToString(), node, x => x.LastHealthCheck, DateTimeOffset.UtcNow);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.Patch<WolverineNode, DateTimeOffset>(nodeId.ToString(), x => x.LastHealthCheck, lastHeartbeatTime);
        await session.SaveChangesAsync();
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


}

public class NodeSequence
{
    public static readonly string SequenceId = "nodes/sequence";
    
    public string Id { get; set; } = SequenceId;
    public int Count { get; set; }
}
