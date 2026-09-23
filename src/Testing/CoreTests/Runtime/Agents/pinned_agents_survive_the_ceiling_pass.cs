using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4591. AssignmentGrid.ApplyRestrictions runs BEFORE the families distribute, so a ceiling pass
/// that detaches whatever sits above the line undoes an operator's pin — and ApplyRestrictions puts it
/// back on the next evaluation, and the pass takes it off again. A churn loop that emits commands
/// forever and never converges, for as long as the pin sits on a node above its share.
/// </summary>
public class pinned_agents_survive_the_ceiling_pass
{
    private readonly Uri blue1 = new("blue://1");
    private readonly Uri blue2 = new("blue://2");
    private readonly Uri blue3 = new("blue://3");
    private readonly Uri blue4 = new("blue://4");
    private readonly Uri blue5 = new("blue://5");
    private readonly Uri blue6 = new("blue://6");

    [Fact]
    public void blue_green_distribution_leaves_pins_alone()
    {
        var grid = new AssignmentGrid();

        // Two things this arrangement has to get right to see the bug at all.
        //
        // First, the pins sit at the END of node1's agent list, which is where ApplyRestrictions
        // actually puts them -- it calls node.TryAssign(uri), which appends. That position IS the bug:
        // Skip(maximum) reaches exactly the tail, so a pin applied this evaluation is the first thing
        // the ceiling pass throws away.
        //
        // Second, the pinned agents need somewhere else they could go. A pinned agent whose only
        // capable node is the one it is pinned to gets detached and then immediately re-placed on that
        // same node later in the pass, so the final assignment looks untouched even though the
        // evaluation churned. node2 is equally capable, which is what makes the detach observable.
        //
        // node3 declares only blue6, which is what makes capabilities heterogeneous and sends this
        // through the blue/green path instead of delegating to DistributeEvenly.
        var all = new[] { blue1, blue2, blue3, blue4, blue5, blue6 };

        var node1 = grid.WithNode(1, Guid.NewGuid())
            .HasCapabilities(all)
            .Running(blue3, blue4, blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid())
            .HasCapabilities(all);
        var node3 = grid.WithNode(3, Guid.NewGuid())
            .HasCapabilities(new[] { blue6 })
            .Running(blue5, blue6);

        grid.AgentFor(blue1).IsPinned = true;
        grid.AgentFor(blue2).IsPinned = true;

        grid.DistributeEvenlyWithBlueGreenSemantics("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue2).AssignedNode.ShouldBe(node1);
    }

    [Fact]
    public void affinity_distribution_leaves_pins_alone()
    {
        var grid = new AssignmentGrid();

        // Pins at the tail, as ApplyRestrictions leaves them -- see the blue/green case above.
        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue4, blue5, blue6, blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3);

        grid.AgentFor(blue1).IsPinned = true;
        grid.AgentFor(blue2).IsPinned = true;

        // No preferences, so every agent falls into the evenly-spread remainder and meets the ceiling.
        grid.DistributeEvenlyWithAffinity("blue", _ => null, _ => true);

        grid.AgentFor(blue1).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue2).AssignedNode.ShouldBe(node1);
    }

    /// <summary>
    /// The churn this is really about: a pin above the ceiling has to be a FIXED POINT. One evaluation
    /// leaving it in place proves little if the next one moves it.
    /// </summary>
    [Fact]
    public void a_pin_above_the_ceiling_is_a_fixed_point_across_evaluations()
    {
        var node1Id = Guid.NewGuid();
        var node2Id = Guid.NewGuid();

        var all = new[] { blue1, blue2, blue3, blue4, blue5, blue6 };

        var on1 = new List<Uri> { blue4, blue5, blue6 };
        var on2 = new List<Uri> { blue1, blue2, blue3 };

        for (var tick = 0; tick < 3; tick++)
        {
            var grid = new AssignmentGrid();
            var node1 = grid.WithNode(1, node1Id).HasCapabilities(all).Running(on1.ToArray());
            var node2 = grid.WithNode(2, node2Id).HasCapabilities(all).Running(on2.ToArray());

            // What ApplyRestrictions does at the top of every evaluation. TryAssign only succeeds for
            // an agent the node DECLARES -- hence the capabilities above -- and it appends, so on the
            // first tick these land at the tail of node1's list, exactly where Skip(maximum) bites.
            foreach (var uri in new[] { blue1, blue2, blue3 })
            {
                node1.TryAssign(uri).ShouldBeTrue();
                grid.AgentFor(uri).IsPinned = true;
            }

            grid.DistributeEvenly("blue");

            on1 = node1.Agents.Select(x => x.Uri).ToList();
            on2 = node2.Agents.Select(x => x.Uri).ToList();

            on1.ShouldContain(blue1);
            on1.ShouldContain(blue2);
            on1.ShouldContain(blue3);
        }

        // Settled: node1 holds exactly its three pins, and the unpinned agents it was over the
        // ceiling by moved to node2 once and stopped moving.
        on1.Count.ShouldBe(3);
        on2.Count.ShouldBe(3);
    }
}
