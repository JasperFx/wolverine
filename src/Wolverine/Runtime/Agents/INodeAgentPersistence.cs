namespace Wolverine.Runtime.Agents;

public interface INodeAgentPersistence
{
    Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken);
    Task DeleteAsync(Guid nodeId);
    Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken);

    Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken);
    Task<Guid?> MarkNodeAsLeaderAsync(Guid? originalLeader, Guid id);
    Task<IReadOnlyList<Uri>> LoadAllOtherNodeControlUrisAsync(Guid selfId);
    Task<WolverineNode?> LoadNodeAsync(Guid nodeId);
    Task MarkHealthCheckAsync(Guid nodeId);
}