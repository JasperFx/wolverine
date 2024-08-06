using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public record CheckAgentHealth : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return runtime.Agents.DoHealthChecksAsync();
    }

    public byte[] Write()
    {
        return [];
    }

    public static object Read(byte[] bytes)
    {
        return new CheckAgentHealth();
    }
}

public partial class NodeAgentController
{
    public bool IsLeader { get; private set; }

    public async Task<AgentCommands> DoHealthChecksAsync()
    {
        if (_cancellation.IsCancellationRequested)
        {
            return AgentCommands.Empty;
        }

        // write health check regardless
        await _persistence.MarkHealthCheckAsync(_runtime.Options.UniqueNodeId);

        var nodes = await _persistence.LoadAllNodesAsync(_cancellation.Token);

        // Check for stale nodes that are no longer writing health checks
        var staleTime = DateTimeOffset.UtcNow.Subtract(_runtime.Options.Durability.StaleNodeTimeout);
        var staleNodes = nodes.Where(x => x.LastHealthCheck < staleTime).ToArray();
        nodes = nodes.Where(x => !staleNodes.Contains(x)).ToList();

        // Do it no matter what
        await ejectStaleNodes(staleNodes);

        if (_persistence.HasLeadershipLock())
        {
            IsLeader = true;
            return await EvaluateAssignmentsAsync(nodes);
        }

        if (await _persistence.TryAttainLeadershipLockAsync(_cancellation.Token))
        {
            try
            {
                // If this fails, release the leadership lock!
                await _persistence.AddAssignmentAsync(_runtime.Options.UniqueNodeId, LeaderUri,
                    _cancellation.Token);
            }
            catch (Exception)
            {
                await _persistence.ReleaseLeadershipLockAsync();
                throw;
            }

            IsLeader = true;

            _logger.LogInformation("Node {NodeNumber} successfully assumed leadership", _runtime.Options.UniqueNodeId);
            await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options,
                NodeRecordType.LeadershipAssumed, LeaderUri));

            return await EvaluateAssignmentsAsync(nodes);
        }

        return AgentCommands.Empty;
    }

    private async Task ejectStaleNodes(IReadOnlyList<WolverineNode> staleNodes)
    {
        foreach (var staleNode in staleNodes)
        {
            await _persistence.DeleteAsync(staleNode.Id);
        }

        if (staleNodes.Any())
        {
            var records = staleNodes.Select(x => new NodeRecord
            {
                NodeNumber = x.AssignedNodeId,
                RecordType = NodeRecordType.DormantNodeEjected,
                Description = "Health check on Node " + _runtime.Options.Durability.AssignedNodeNumber
            }).ToArray();

            await _persistence.LogRecordsAsync(records);
        }
    }

    public void CancelHeartbeatChecking()
    {
        _cancellation.Cancel();
    }
}