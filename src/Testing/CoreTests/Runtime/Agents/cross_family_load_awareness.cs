using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3877: each agent family distributes its own scheme in its own pass, so a distribution pass has to
/// take the load a node already carries from the *other* passes into account when it is otherwise free to
/// choose. The motivating case is GlobalPartitioned slot listeners (wolverine-listener://) whose slot count
/// does not divide evenly across the nodes: the node holding fewer slots should be the one that picks up
/// the next family's work.
/// </summary>
public class cross_family_load_awareness
{
    // Stands in for GlobalPartitioned slot listeners: 5 slots is a legal PartitionSlots value, and 5 over
    // 3 nodes is the uneven case that makes one node the "hot" slot holder.
    private readonly Uri slot1 = new("wolverine-listener://rabbitmq/orders1");
    private readonly Uri slot2 = new("wolverine-listener://rabbitmq/orders2");
    private readonly Uri slot3 = new("wolverine-listener://rabbitmq/orders3");
    private readonly Uri slot4 = new("wolverine-listener://rabbitmq/orders4");
    private readonly Uri slot5 = new("wolverine-listener://rabbitmq/orders5");

    private readonly Uri evaluator1 = new("evaluator://1");
    private readonly Uri evaluator2 = new("evaluator://2");

    [Fact]
    public void second_family_prefers_the_node_holding_fewer_partition_slots()
    {
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());
        var node3 = grid.WithNode(3, Guid.NewGuid());

        // The partition slot family goes first, exactly as ExclusiveListenerFamily does
        grid.WithAgents(slot1, slot2, slot3, slot4, slot5);
        grid.DistributeEvenly("wolverine-listener");

        // 5 slots over 3 nodes cannot be even -- somebody ends up with one
        node1.Agents.Count.ShouldBe(2);
        node2.Agents.Count.ShouldBe(2);
        node3.Agents.Count.ShouldBe(1);

        // Now a second, unrelated family with fewer agents than nodes. Every one of these lands in the
        // remainder pass, where the pass is completely free to choose -- so it should choose the node that
        // is not already holding two slots.
        grid.WithAgents(evaluator1, evaluator2);
        grid.DistributeEvenly("evaluator");

        node3.Agents.Select(x => x.Uri).ShouldContain(evaluator1);

        // ...and the totals are balanced rather than 3/3/1
        node1.Agents.Count.ShouldBe(3);
        node2.Agents.Count.ShouldBe(2);
        node3.Agents.Count.ShouldBe(2);
    }

    [Fact]
    public void minimum_fill_pass_also_prefers_the_least_loaded_node()
    {
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());
        var node3 = grid.WithNode(3, Guid.NewGuid());

        grid.WithAgents(slot1, slot2, slot3, slot4, slot5);
        grid.DistributeEvenly("wolverine-listener");

        // 6 agents over 3 nodes has a minimum of 2, so this exercises the fill-to-minimum pass rather
        // than the remainder pass. Balance within the scheme is unconditional, but the node that was
        // already carrying the fewest slots should be filled first.
        var evaluators = Enumerable.Range(1, 6).Select(i => new Uri($"evaluator://{i}")).ToArray();
        grid.WithAgents(evaluators);
        grid.DistributeEvenly("evaluator");

        node1.Agents.Count(x => x.Uri.Scheme == "evaluator").ShouldBe(2);
        node2.Agents.Count(x => x.Uri.Scheme == "evaluator").ShouldBe(2);
        node3.Agents.Count(x => x.Uri.Scheme == "evaluator").ShouldBe(2);

        // The least loaded node was served first
        node3.Agents.Select(x => x.Uri).ShouldContain(evaluators[0]);
    }

    [Fact]
    public void does_not_move_already_assigned_agents_when_re_evaluated()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithNode(3, Guid.NewGuid());

        grid.WithAgents(slot1, slot2, slot3, slot4, slot5);
        grid.DistributeEvenly("wolverine-listener");

        grid.WithAgents(evaluator1, evaluator2);
        grid.DistributeEvenly("evaluator");

        var before = grid.AllAgents.ToDictionary(x => x.Uri, x => x.AssignedNode);

        // Re-running the same evaluation on a settled grid must be a no-op. The load-aware ordering is a
        // tie-break over placements that are otherwise free, so it cannot cause reassignment churn.
        grid.DistributeEvenly("wolverine-listener");
        grid.DistributeEvenly("evaluator");

        foreach (var agent in grid.AllAgents)
        {
            agent.AssignedNode.ShouldBeSameAs(before[agent.Uri]);
        }
    }
}
