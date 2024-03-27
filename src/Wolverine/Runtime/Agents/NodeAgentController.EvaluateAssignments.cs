using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public record EvaluateAssignments : IAgentCommand
{
    private readonly NodeAgentController _controller;

    public EvaluateAssignments(NodeAgentController controller)
    {
        _controller = controller;
    }

    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return _controller.EvaluateAssignmentsAsync();
    }
}

public partial class NodeAgentController 
{
    public AssignmentGrid? LastAssignments { get; internal set; }

    // Tested w/ integration tests all the way
    public async Task<AgentCommands> EvaluateAssignmentsAsync()
    {
        var grid = AssignmentGrid.ForTracker(_tracker);

        // Load all other nodes to also pick up capabilities that might not exist in the leader
        // This is specifically to enable blue/green deployment
        var nodes = await _persistence.LoadAllNodesAsync(_cancellation);
        var capabilities = nodes.SelectMany(x => x.Capabilities).Distinct().ToArray();
        grid.WithAgents(capabilities);

        foreach (var controller in _agentFamilies.Values)
        {
            try
            {
                var allAgents = await controller.AllKnownAgentsAsync();
                grid.WithAgents(allAgents
                    .ToArray()); // Just in case something has gotten lost, and this is master anyway

                await controller.EvaluateAssignmentsAsync(grid);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to reevaluate agent assignments for '{Scheme}' agents",
                    controller.Scheme);
            }
        }

        var commands = new AgentCommands();

        foreach (var agent in grid.AllAgents)
        {
            if (agent.TryBuildAssignmentCommand(out var agentCommand))
            {
                commands.Add(agentCommand);
            }
        }

        var records = commands.Select(x => new NodeRecord
        {
            NodeNumber = _runtime.Options.Durability.AssignedNodeNumber,
            RecordType = NodeRecordType.AssignmentChanged,
            Description = x.ToString()
        }).ToArray();

        await _persistence.LogRecordsAsync(records);

        _tracker.Publish(new AgentAssignmentsChanged(commands, grid));

        LastAssignments = grid;
        LastAssignments.EvaluationTime = DateTimeOffset.UtcNow;

        if (commands.Any())
        {
            batchCommands(commands);
        }

        return commands;
    }

    private static void batchCommands(List<IAgentCommand> commands)
    {
        foreach (var group in commands.OfType<AssignAgent>().GroupBy(x => x.NodeId).Where(x => x.Count() > 1).ToArray())
        {
            var assignAgents = new AssignAgents(group.Key, group.Select(x => x.AgentUri).ToArray());

            foreach (var message in group) commands.Remove(message);

            commands.Add(assignAgents);
        }

        foreach (var group in commands.OfType<StopRemoteAgent>().GroupBy(x => x.NodeId).Where(x => x.Count() > 1)
                     .ToArray())
        {
            var stopAgents = new StopRemoteAgents(group.Key, group.Select(x => x.AgentUri).ToArray());

            foreach (var message in group) commands.Remove(message);

            commands.Add(stopAgents);
        }
    }

    private Task requestAssignmentEvaluationAsync() =>
        // This buffers requests to reevaluate and reassign node
        // assignments so that the system isn't repeatedly redoing
        // this work as a node cluster spins up
        _assignmentBufferBlock.SendAsync(new EvaluateAssignments(this));
}