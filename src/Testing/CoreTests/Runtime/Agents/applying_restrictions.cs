using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class applying_restrictions
{
    private readonly Uri blue1 = new Uri("blue://1");
    private readonly Uri blue2 = new Uri("blue://2");
    private readonly Uri blue3 = new Uri("blue://3");
    private readonly Uri blue4 = new Uri("blue://4");
    private readonly Uri blue5 = new Uri("blue://5");
    private readonly Uri blue6 = new Uri("blue://6");
    private readonly Uri blue7 = new Uri("blue://7");
    private readonly Uri blue8 = new Uri("blue://8");
    private readonly Uri blue9 = new Uri("blue://9");
    private readonly Uri blue10 = new Uri("blue://10");
    private readonly Uri blue11 = new Uri("blue://11");
    private readonly Uri blue12 = new Uri("blue://12");

    private readonly Uri green1 = new Uri("green://1");
    private readonly Uri green2 = new Uri("green://2");
    private readonly Uri green3 = new Uri("green://3");
    private readonly Uri green4 = new Uri("green://4");
    private readonly Uri green5 = new Uri("green://5");
    private readonly Uri green6 = new Uri("green://6");
    private readonly Uri green7 = new Uri("green://7");
    private readonly Uri green8 = new Uri("green://8");
    private readonly Uri green9 = new Uri("green://9");
    private readonly Uri green10 = new Uri("green://10");
    private readonly Uri green11 = new Uri("green://11");
    private readonly Uri green12 = new Uri("green://12");

    private readonly Uri red1 = new Uri("red://1");
    private readonly Uri red2 = new Uri("red://2");
    private readonly Uri red3 = new Uri("red://3");
    private readonly Uri red4 = new Uri("red://4");
    private readonly Uri red5 = new Uri("red://5");
    private readonly Uri red6 = new Uri("red://6");
    private readonly Uri red7 = new Uri("red://7");
    private readonly Uri red8 = new Uri("red://8");
    private readonly Uri red9 = new Uri("red://9");
    private readonly Uri red10 = new Uri("red://10");
    private readonly Uri red11 = new Uri("red://11");
    private readonly Uri red12 = new Uri("red://12");

    [Fact]
    public void add_pinned_restriction_with_no_assignments()
    {
        var grid = new AssignmentGrid();
        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        Uri[] capabilities = [blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12];
        
        var node1 = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(capabilities);
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(capabilities);
        var node3 = grid.WithNode(3, Guid.NewGuid()).HasCapabilities(capabilities);
        var node4 = grid.WithNode(4, Guid.NewGuid()).HasCapabilities(capabilities);

        var restrictions = new AgentRestrictions([]);
        restrictions.PinAgent(blue10, 4);
        restrictions.PinAgent(blue11, 3);
        
        grid.ApplyRestrictions(restrictions);

        grid.AgentFor(blue10).AssignedNode.ShouldBe(node4);
        grid.AgentFor(blue10).IsPinned.ShouldBeTrue();
        
        grid.AgentFor(blue11).AssignedNode.ShouldBe(node3);
        grid.AgentFor(blue11).IsPinned.ShouldBeTrue();
    }
    
    [Fact]
    public void add_pinned_restriction_with_current_assignments_that_does_not_move()
    {
        var grid = new AssignmentGrid();
        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        Uri[] capabilities = [blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12];
        
        var node1 = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(capabilities);
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(capabilities);
        var node3 = grid.WithNode(3, Guid.NewGuid()).HasCapabilities(capabilities);
        var node4 = grid.WithNode(4, Guid.NewGuid()).HasCapabilities(capabilities);
        
        grid.NodeFor(4).Assign(blue10);
        grid.NodeFor(3).Assign(blue11);

        var restrictions = new AgentRestrictions([]);
        restrictions.PinAgent(blue10, 4);
        restrictions.PinAgent(blue11, 3);
        
        grid.ApplyRestrictions(restrictions);
        
        grid.DistributeEvenly("blue");

        grid.AgentFor(blue10).AssignedNode.ShouldBe(node4);
        grid.AgentFor(blue10).IsPinned.ShouldBeTrue();
        
        grid.AgentFor(blue11).AssignedNode.ShouldBe(node3);
        grid.AgentFor(blue11).IsPinned.ShouldBeTrue();
    }
    
    [Fact]
    public void add_pinned_restriction_with_current_assignments_that_forces_a_move()
    {
        var grid = new AssignmentGrid();
        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        Uri[] capabilities = [blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12];
        
        var node1 = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(capabilities);
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(capabilities);
        var node3 = grid.WithNode(3, Guid.NewGuid()).HasCapabilities(capabilities);
        var node4 = grid.WithNode(4, Guid.NewGuid()).HasCapabilities(capabilities);
        
        grid.NodeFor(1).Assign(blue10);
        grid.NodeFor(2).Assign(blue11);

        var restrictions = new AgentRestrictions([]);
        restrictions.PinAgent(blue10, 4);
        restrictions.PinAgent(blue11, 3);
        
        grid.ApplyRestrictions(restrictions);
        
        grid.DistributeEvenly("blue");

        grid.AgentFor(blue10).AssignedNode.ShouldBe(node4);
        grid.AgentFor(blue10).IsPinned.ShouldBeTrue();
        
        grid.AgentFor(blue11).AssignedNode.ShouldBe(node3);
        grid.AgentFor(blue11).IsPinned.ShouldBeTrue();
    }

    [Fact]
    public void distribute_evenly_from_nothing_respects_paused_agents()
    {
        var grid = new AssignmentGrid();
        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        Uri[] capabilities = [blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12];
        
        var node1 = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(capabilities);
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(capabilities);
        var node3 = grid.WithNode(3, Guid.NewGuid()).HasCapabilities(capabilities);
        var node4 = grid.WithNode(4, Guid.NewGuid()).HasCapabilities(capabilities);

        var restrictions = new AgentRestrictions([]);
        
        restrictions.PauseAgent(blue1);
        restrictions.PauseAgent(blue12);
        
        grid.ApplyRestrictions(restrictions);
        
        grid.DistributeEvenly("blue");

        var blue1Agent = grid.AgentFor(blue1);
        blue1Agent.IsPaused.ShouldBeTrue();
        blue1Agent.AssignedNode.ShouldBeNull();
        
        var blue12Agent = grid.AgentFor(blue12);
        blue12Agent.IsPaused.ShouldBeTrue();
        blue12Agent.AssignedNode.ShouldBeNull();

        grid.AgentFor(blue2).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue3).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue4).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue5).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue6).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue7).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue8).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue9).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue10).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue11).AssignedNode.ShouldNotBeNull();
    }
    
    [Fact]
    public void pause_assigned_agent_then_distribute_evenly_respects_paused_agents()
    {
        var grid = new AssignmentGrid();
        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        Uri[] capabilities = [blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12];
        
        var node1 = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(capabilities);
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(capabilities);
        var node3 = grid.WithNode(3, Guid.NewGuid()).HasCapabilities(capabilities);
        var node4 = grid.WithNode(4, Guid.NewGuid()).HasCapabilities(capabilities);
        
        node1.Assign(blue1);
        node2.Assign(blue12);

        var restrictions = new AgentRestrictions([]);
        
        restrictions.PauseAgent(blue1);
        restrictions.PauseAgent(blue12);
        
        grid.ApplyRestrictions(restrictions);
        
        grid.DistributeEvenly("blue");

        var blue1Agent = grid.AgentFor(blue1);
        blue1Agent.IsPaused.ShouldBeTrue();
        blue1Agent.AssignedNode.ShouldBeNull();
        
        var blue12Agent = grid.AgentFor(blue12);
        blue12Agent.IsPaused.ShouldBeTrue();
        blue12Agent.AssignedNode.ShouldBeNull();

        grid.AgentFor(blue2).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue3).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue4).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue5).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue6).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue7).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue8).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue9).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue10).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue11).AssignedNode.ShouldNotBeNull();
    }

    [Fact]
    public void mixed_pins_and_pauses()
    {
        var grid = new AssignmentGrid();
        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        Uri[] capabilities = [blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12];
        
        var node1 = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(capabilities);
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(capabilities);
        var node3 = grid.WithNode(3, Guid.NewGuid()).HasCapabilities(capabilities);
        var node4 = grid.WithNode(4, Guid.NewGuid()).HasCapabilities(capabilities);
        
        node1.Assign(blue1);
        node2.Assign(blue12);

        var restrictions = new AgentRestrictions([]);
        
        restrictions.PauseAgent(blue1);
        restrictions.PauseAgent(blue12);
        restrictions.PinAgent(blue2, 3);
        restrictions.PinAgent(blue4, 3);
        
        grid.ApplyRestrictions(restrictions);
        
        grid.DistributeEvenly("blue");

        var blue1Agent = grid.AgentFor(blue1);
        blue1Agent.IsPaused.ShouldBeTrue();
        blue1Agent.AssignedNode.ShouldBeNull();
        
        var blue12Agent = grid.AgentFor(blue12);
        blue12Agent.IsPaused.ShouldBeTrue();
        blue12Agent.AssignedNode.ShouldBeNull();

        grid.AgentFor(blue2).AssignedNode.AssignedId.ShouldBe(3);
        grid.AgentFor(blue3).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue4).AssignedNode.AssignedId.ShouldBe(3);
        grid.AgentFor(blue5).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue6).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue7).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue8).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue9).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue10).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue11).AssignedNode.ShouldNotBeNull();
    }


    /*
     * TODO
     * Have a pin to a node that no longer exists, gets freed up and reassigned
     * Pinned assignments stay put
     * Paused agents stay unassigned event with 1 node
     * Paused agent when agent is already unassigned
     * Paused agent when agent is assigned, does not get re-assigned
     * 
     * 
     */
}