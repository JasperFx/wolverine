using TestingSupport;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class assigning_agent_logic
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
    public void add_node_with_agents()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);
        
        grid.Nodes.Count.ShouldBe(3);
        
        grid.AllAgents.Count.ShouldBe(6);

        grid.WithAgents(green9);
        
        grid.AllAgents.Count.ShouldBe(7);
        
        node1.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue1, blue2);
    }

    [Fact]
    public void unassigned_agent_logic()
    {
        var grid = new AssignmentGrid();
        grid.WithAgents(blue1, blue3);
        
        grid.UnassignedAgents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue1, blue3);

        grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        
        // should show the uri as no longer assigned
        grid.UnassignedAgents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue3);
        
        grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        
        grid.UnassignedAgents.ShouldBeEmpty();
        
        grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);
        grid.WithAgents(red1);

        // Add an agent, but it's already assigned, so...
        grid.UnassignedAgents.ShouldBeEmpty();
    }

    [Fact]
    public void detach_agent()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);

        var agent = grid.AgentFor(blue2);
        agent.Detach();

        grid.UnassignedAgents.Single().Uri.ShouldBe(blue2);
        agent.AssignedNode.ShouldBeNull();
        node1.Agents.ShouldNotContain(x => x.Uri == blue2);
    }

    [Fact]
    public void detach_from_node()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);

        var agent = grid.AgentFor(blue2);

        node1.Detach(agent);
        
        grid.UnassignedAgents.Single().Uri.ShouldBe(blue2);
        agent.AssignedNode.ShouldBeNull();
        node1.Agents.ShouldNotContain(x => x.Uri == blue2);
    }

    [Fact]
    public void assign_from_node_when_agent_does_not_already_exist()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);

        node3.Assign(blue8);

        node3.Agents.ShouldContain(x => x.Uri == blue8);

        var agent = grid.AgentFor(blue8);
        
        agent.OriginalNode.ShouldBeNull();
        agent.AssignedNode.ShouldBe(node3);
        
        node3.Agents.ShouldContain(agent);
    }
    
    [Fact]
    public void assign_from_node_when_agent_exists_as_unattached()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);

        grid.WithAgents(blue8);
        var agent = grid.AgentFor(blue8);
        
        node3.Assign(blue8);

        node3.Agents.ShouldContain(x => x.Uri == blue8);

        agent.OriginalNode.ShouldBeNull();
        agent.AssignedNode.ShouldBe(node3);
        
        node3.Agents.ShouldContain(agent);
    }

    [Fact]
    public void reassign_an_agent()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);
        var node4 = grid.WithNode(4, Guid.NewGuid());

        node4.Assign(blue1);

        var agent = grid.AgentFor(blue1);

        node1.Agents.ShouldNotContain(x => x.Uri == blue1);
        node4.Agents.Single().ShouldBe(agent);

        agent.OriginalNode.ShouldBe(node1);
        agent.AssignedNode.ShouldBe(node4);
    }

    [Fact]
    public void agent_builds_no_command_with_no_assignment_before_or_after()
    {
        var grid = new AssignmentGrid();
        var agent = grid.WithAgent(blue9);
        agent.OriginalNode.ShouldBeNull();
        agent.AssignedNode.ShouldBeNull();
        
        agent.TryBuildAssignmentCommand(out var command).ShouldBeFalse();
    }

    [Fact]
    public void agent_builds_no_command_with_no_changed_assignment()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);

        var agent = grid.AgentFor(blue1);
        
        agent.AssignedNode.ShouldBe(agent.OriginalNode);
        
        agent.TryBuildAssignmentCommand(out var command).ShouldBeFalse();
    }

    [Fact]
    public void should_try_to_start_agent_that_was_not_already_assigned()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node4 = grid.WithNode(4, Guid.NewGuid());

        var agent = grid.WithAgent(green1);
        
        node4.Assign(agent);
        
        agent.OriginalNode.ShouldBeNull();
        agent.AssignedNode.ShouldBe(node4);
        
        agent.TryBuildAssignmentCommand(out var command).ShouldBeTrue();
        
        command.ShouldBe(new AssignAgent(agent.Uri, node4.NodeId));
    }

    [Fact]
    public void should_stop_an_agent_that_was_previously_running()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);

        var agent = grid.AgentFor(blue1);
        
        agent.Detach();

        agent.OriginalNode.ShouldNotBeNull();
        agent.AssignedNode.ShouldBeNull();
        
        agent.TryBuildAssignmentCommand(out var command).ShouldBeTrue();
        
        command.ShouldBe(new StopRemoteAgent(agent.Uri, node1.NodeId));
    }

    [Fact]
    public void reassign_a_currently_running_node()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node4 = grid.WithNode(4, Guid.NewGuid());

        var agent = grid.AgentFor(blue2);
        
        node4.Assign(agent);
        
        agent.OriginalNode.ShouldBe(node1);
        agent.AssignedNode.ShouldBe(node4);
        
        agent.TryBuildAssignmentCommand(out var command).ShouldBeTrue();
        
        command.ShouldBe(new ReassignAgent(agent.Uri, node1.NodeId, node4.NodeId));
    }
    
    [Fact]
    public void distribute_evenly_from_scratch_one_node()
    {
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());


        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        grid.DistributeEvenly("blue");
        
        node1.Agents.Count.ShouldBe(12);

        var all = new Uri[] { blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12 };
        foreach (var agentUri in all)
        {
            grid.AgentFor(agentUri).AssignedNode.ShouldNotBeNull();
        }
    }

    [Fact]
    public void distribute_evenly_from_scratch_multiple_nodes()
    {
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());
        var node3 = grid.WithNode(3, Guid.NewGuid());
        var node4 = grid.WithNode(4, Guid.NewGuid());

        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        grid.DistributeEvenly("blue");
        
        node1.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue1, blue2, blue3);
        node2.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue4, blue5, blue6);
        node3.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue7, blue8, blue9);
        node4.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue10, blue11, blue12);
        
        var all = new Uri[] { blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12 };
        foreach (var agentUri in all)
        {
            grid.AgentFor(agentUri).AssignedNode.ShouldNotBeNull();
        }
    }

    [Fact]
    public void distribute_evenly_from_scratch_multiple_nodes_remainder()
    {
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        node1.IsLeader = true; // put fewer on leader
        
        var node2 = grid.WithNode(2, Guid.NewGuid());
        var node3 = grid.WithNode(3, Guid.NewGuid());
        var node4 = grid.WithNode(4, Guid.NewGuid());
        var node5 = grid.WithNode(5, Guid.NewGuid());

        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        grid.DistributeEvenly("blue");
        
        // this will be broken. 
        node1.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue1, blue2);
        node2.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue3, blue4, blue11);
        node3.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue5, blue6, blue12);
        node4.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue7, blue8);
        node5.Agents.Select(x => x.Uri).ShouldHaveTheSameElementsAs(blue9, blue10);
        
        var all = new Uri[] { blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12 };
        foreach (var agentUri in all)
        {
            grid.AgentFor(agentUri).AssignedNode.ShouldNotBeNull();
        }
    }

    [Fact]
    public void distribute_evenly_from_scratch_multiple_nodes_then_distribute_again_with_extra_nodes()
    {
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());
        var node3 = grid.WithNode(3, Guid.NewGuid());
        var node4 = grid.WithNode(4, Guid.NewGuid());

        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        grid.DistributeEvenly("blue");
        
        // Add more nodes
        var node5 = grid.WithNode(5, Guid.NewGuid());
        var node6 = grid.WithNode(6, Guid.NewGuid());
        
        grid.DistributeEvenly("blue");
        
        node1.Agents.Select(x => x.Uri).Count().ShouldBe(2);
        node2.Agents.Select(x => x.Uri).Count().ShouldBe(2);
        node3.Agents.Select(x => x.Uri).Count().ShouldBe(2);
        node4.Agents.Select(x => x.Uri).Count().ShouldBe(2);
        node5.Agents.Select(x => x.Uri).Count().ShouldBe(2);
        node6.Agents.Select(x => x.Uri).Count().ShouldBe(2);

        var all = new Uri[] { blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12 };
        foreach (var agentUri in all)
        {
            grid.AgentFor(agentUri).AssignedNode.ShouldNotBeNull();
        }
    }
    
    [Fact]
    public void distribute_evenly_from_scratch_multiple_nodes_then_remove_nodes()
    {
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        node1.IsLeader = true; // put fewer on leader
        
        var node2 = grid.WithNode(2, Guid.NewGuid());
        var node3 = grid.WithNode(3, Guid.NewGuid());
        var node4 = grid.WithNode(4, Guid.NewGuid());
        var node5 = grid.WithNode(5, Guid.NewGuid());

        grid.WithAgents(blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12);

        grid.DistributeEvenly("blue");

        grid.Remove(node4);
        grid.Remove(node5);
        
        grid.UnassignedAgents.ShouldNotBeEmpty();
        
        grid.DistributeEvenly("blue");
        
        // this will be broken. 
        node1.Agents.Select(x => x.Uri).Count().ShouldBe(4);
        node2.Agents.Select(x => x.Uri).Count().ShouldBe(4);
        node3.Agents.Select(x => x.Uri).Count().ShouldBe(4);

        
        var all = new Uri[] { blue1, blue2, blue3, blue4, blue5, blue6, blue7, blue8, blue9, blue10, blue11, blue12 };
        foreach (var agentUri in all)
        {
            grid.AgentFor(agentUri).AssignedNode.ShouldNotBeNull();
        }
    }

    [Fact]
    public void find_delta_with_missing_agent()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);

        var dict = grid.CompileAssignments();

        dict.Remove(blue2);

        var delta = grid.FindDelta(dict).ToArray();
        delta.Single().ShouldBe(new AssignAgent(blue2, node1.NodeId));
    }

    [Fact]
    public async Task find_delta_with_extra_agent()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);

        var dict = grid.CompileAssignments();

        dict.Add(green8, node2.NodeId);
        
        var delta = grid.FindDelta(dict).ToArray();
        delta.Single().ShouldBe(new StopRemoteAgent(green8, node2.NodeId));
        
    }

    [Fact]
    public void find_delta_when_agent_is_running_on_wrong_node()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);

        var dict = grid.CompileAssignments();

        dict[red1] = node1.NodeId;
        
        var delta = grid.FindDelta(dict).ToArray();
        delta.Single().ShouldBe(new ReassignAgent(red1, node1.NodeId, node3.NodeId));

    }

    [Fact]
    public void run_on_leader()
    {
        var grid = new AssignmentGrid();
        
        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue3, blue4);
        var node3 = grid.WithNode(3, Guid.NewGuid()).Running(red1, red2);

        node3.IsLeader = true;
        
        grid.RunOnLeader(blue5);
        grid.RunOnLeader(blue1);
        
        grid.AgentFor(blue5).AssignedNode.ShouldBe(node3);
        grid.AgentFor(blue1).AssignedNode.ShouldBe(node3);
        
    }
}