using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Locks down the Layer 3 fix for GH-2602. When two <see cref="WolverineNode"/>
/// rows both list the same agent in their <c>ActiveAgents</c> — the residue of
/// a brief leader split-brain — the assignment grid must record the duplicate
/// so the (now-correct) leader can heal it via <c>StopRemoteAgent</c>. Before
/// the fix, the dictionary write in <c>Node.Running</c> silently overwrote
/// the first node's entry and the duplicate became invisible to <c>FindDelta</c>.
/// </summary>
public class duplicate_agent_split_brain_detection
{
    private readonly Uri agent1 = new Uri("blue://1");
    private readonly Uri agent2 = new Uri("blue://2");

    [Fact]
    public void records_duplicate_when_same_agent_runs_on_two_nodes()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(agent1);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(agent1);

        grid.DuplicateAgentReports.Count.ShouldBe(1);

        var report = grid.DuplicateAgentReports.Single();
        report.AgentUri.ShouldBe(agent1);
        report.ExistingNode.ShouldBe(node1);
        report.NewNode.ShouldBe(node2);
    }

    [Fact]
    public void no_report_when_each_agent_runs_on_only_one_node()
    {
        var grid = new AssignmentGrid();

        grid.WithNode(1, Guid.NewGuid()).Running(agent1);
        grid.WithNode(2, Guid.NewGuid()).Running(agent2);

        grid.DuplicateAgentReports.ShouldBeEmpty();
    }

    [Fact]
    public void no_report_when_same_node_lists_agent_twice()
    {
        // A single Running call with duplicates within the same node — odd
        // input, but not the split-brain we're trying to detect.
        var grid = new AssignmentGrid();

        grid.WithNode(1, Guid.NewGuid()).Running(agent1, agent1);

        grid.DuplicateAgentReports.ShouldBeEmpty();
    }
}
