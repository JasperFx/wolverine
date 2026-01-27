using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    public AssignmentGrid? LastAssignments { get; internal set; }

    // Tested w/ integration tests all the way
    public async Task<AgentCommands> EvaluateAssignmentsAsync(
        IReadOnlyList<WolverineNode> nodes,
        AgentRestrictions restrictions)
    {
        using var activity = ShouldTraceHealthCheck() 
            ? WolverineTracing.ActivitySource.StartActivity("wolverine_node_assignments") 
            : null;

        // Not sure how this *could* happen, but we had a report of it happening in production
        // probably because someone messed w/ the database though
        if (!nodes.Any())
        {
            // At least use the current node
            nodes = new List<WolverineNode> { WolverineNode.For(_runtime.Options) };
            nodes[0].AssignAgents([LeaderUri]);
        }
        
        var grid = new AssignmentGrid();

        var capabilities = nodes.SelectMany(x => x.Capabilities).Distinct().ToArray();
        grid.WithAgents(capabilities);
        
        foreach (var node in nodes)
        {
            grid.WithNode(node);
        }

        foreach (var agentFamily in _agentFamilies.Values)
        {
            try
            {
                var allAgents = await agentFamily.AllKnownAgentsAsync();
                grid.WithAgents(allAgents
                    .ToArray()); // Just in case something has gotten lost, and this is master anyway
                
                // Apply this every time to pick up any agents from above
                grid.ApplyRestrictions(restrictions);
                
                await agentFamily.EvaluateAssignmentsAsync(grid);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to reevaluate agent assignments for '{Scheme}' agents",
                    agentFamily.Scheme);
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

        await _observer.AssignmentsChanged(grid, commands);

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
        foreach (var group in commands.OfType<AssignAgent>().GroupBy(x => x.Destination).Where(x => x.Count() > 1).ToArray())
        {
            var assignAgents = new AssignAgents(group.Key, group.Select(x => x.AgentUri).ToArray());

            foreach (var message in group) commands.Remove(message);

            commands.Add(assignAgents);
        }

        foreach (var group in commands.OfType<StopRemoteAgent>().GroupBy(x => x.Destination).Where(x => x.Count() > 1)
                     .ToArray())
        {
            var stopAgents = new StopRemoteAgents(group.Key, group.Select(x => x.AgentUri).ToArray());

            foreach (var message in group) commands.Remove(message);

            commands.Add(stopAgents);
        }
    }
}