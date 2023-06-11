using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using TestingSupport;
using Wolverine.Logging;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public partial class agent_tracking_behavior
{
    private int _count = 0;
    
    private readonly WolverineTracker theTracker;
    private readonly INodeStateTracker theNodes;

    public agent_tracking_behavior()
    {
        theTracker = new WolverineTracker(NullLogger.Instance);
        theNodes = (INodeStateTracker)theTracker;
    }

    private WolverineNode createNode()
    {
        var id = Guid.NewGuid();
        return new WolverineNode
        {
            Id = id,
            ControlUri = $"dbcontrol://{id}".ToUri(),
            AssignedNodeId = ++_count
        };
    }

    [Fact]
    public void add_nodes()
    {
        var node1 = createNode();
        var node2 = createNode();

        theNodes.Add(node1);
        theNodes.Add(node2);
        
        theTracker.Nodes.Count.ShouldBe(2);
        theTracker.Nodes[node1.Id].ShouldBe(node1);
        theTracker.Nodes[node2.Id].ShouldBe(node2);
    }
    
    [Fact]
    public void add_node_that_is_leader()
    {
        var node1 = createNode();
        var node2 = createNode();
        
        node2.ActiveAgents.Add(NodeAgentController.LeaderUri);

        theNodes.Add(node1);
        theNodes.Add(node2);
        
        theTracker.Leader.ShouldBe(node2);
    }
    
    [Fact]
    public async Task publish_node_started_event()
    {
        var node1 = createNode();
        var node2 = createNode();

        theNodes.Add(node1);
        theNodes.Add(node2);

        var node3 = createNode();
        
        var waiter = theTracker.WaitForNodeEvent(node3.Id, 5.Seconds());
        
        theTracker.Publish(new NodeEvent(node3, NodeEventType.Started));

        var e = await waiter;
        
        e.Type.ShouldBe(NodeEventType.Started);
        theTracker.Nodes[node3.Id].ShouldBe(node3);
    }
    
    [Fact]
    public async Task publish_leader_assumed_event()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();

        theNodes.Add(node1);
        theNodes.Add(node2);
        theNodes.Add(node3);

        
        
        var waiter = theTracker.WaitForNodeEvent(node2.Id, 5.Seconds());
        
        theTracker.Publish(new NodeEvent(node2, NodeEventType.LeadershipAssumed));

        var e = await waiter;
        
        e.Type.ShouldBe(NodeEventType.LeadershipAssumed);
        theTracker.Leader.ShouldBe(node2);
    }
    
    [Fact]
    public async Task publish_node_removed_event_that_is_not_leader()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();

        theNodes.Add(node1);
        theNodes.Add(node2);
        theNodes.Add(node3);

        var waiter = theTracker.WaitForNodeEvent(node2.Id, 15.Seconds());
        
        theTracker.Publish(new NodeEvent(node2, NodeEventType.Exiting));

        var e = await waiter;
        
        e.Type.ShouldBe(NodeEventType.Exiting);
        theTracker.Nodes.Count.ShouldBe(2);
        theTracker.Nodes.ContainsKey(node2.Id).ShouldBeFalse();
    }
    
    [Fact]
    public async Task publish_node_removed_event_that_is_leader()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();

        theNodes.Add(node1);
        theNodes.Add(node2);
        theNodes.Add(node3);
        
        
        theNodes.Publish(new NodeEvent(node2, NodeEventType.LeadershipAssumed));
        await theTracker.WaitForNodeEvent(node2.Id, 15.Seconds());
        
        theTracker.Publish(new NodeEvent(node2, NodeEventType.Exiting));

        await theTracker.DrainAsync();
        
        theTracker.Nodes.Count.ShouldBe(2);
        theTracker.Nodes.ContainsKey(node2.Id).ShouldBeFalse();
        theNodes.Leader.ShouldBeNull();
    }

    [Fact]
    public void set_current()
    {
        var node1 = createNode();
        var node2 = createNode();

        theNodes.Add(node1);
        theNodes.Add(node2);

        var current = createNode();
        theNodes.MarkCurrent(current);
        
        theTracker.Nodes[current.Id].ShouldBe(current);
        theTracker.Self.ShouldBe(current);
    }

    [Fact]
    public void find_oldest_node()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();
        var node4 = createNode();

        theNodes.Add(node1);
        theNodes.Add(node2);
        theNodes.Add(node3);
        theNodes.Add(node4);
        
        theNodes.FindOldestNode().ShouldBe(node1);
    }

    [Fact]
    public async Task mark_leader_by_node_when_it_already_exists()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();
        var node4 = createNode();

        theNodes.Add(node1);
        theNodes.Add(node2);
        theNodes.Add(node3);
        theNodes.Add(node4);

        theNodes.Publish(new NodeEvent(node3, NodeEventType.LeadershipAssumed));
        await theTracker.WaitForNodeEvent(node3.Id, 5.Seconds());
        
        theTracker.Nodes.Count.ShouldBe(4);
        
        theNodes.Leader.ShouldBe(node3);
        
        node3.IsLeader().ShouldBeTrue();
    }
    
    [Fact]
    public async Task mark_leader_by_node_when_it_does_not_already_exist()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();
        var node4 = createNode();

        theNodes.Add(node1);
        theNodes.Add(node2);
        //theNodes.Add(node3);
        theNodes.Add(node4);

        theNodes.Publish(new NodeEvent(node3, NodeEventType.LeadershipAssumed));
        await theTracker.WaitForNodeEvent(node3.Id, 5.Seconds());

        theTracker.Nodes.Count.ShouldBe(4);
        
        
        theNodes.Leader.ShouldBe(node3);
        
        node3.IsLeader().ShouldBeTrue();
    }
    
    [Fact]
    public void find_others()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();
        var node4 = createNode();

        theNodes.MarkCurrent(node1);
        theNodes.Add(node2);
        theNodes.Add(node3);
        theNodes.Add(node4);
        
        theTracker.Nodes[node1.Id].ShouldBe(node1);
        theTracker.Self.ShouldBe(node1);

        theNodes.OtherNodes().OrderBy(x => x.AssignedNodeId).ShouldHaveTheSameElementsAs(node2, node3, node4);
    }

    [Fact]
    public async Task agent_started()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();
        var node4 = createNode();

        theNodes.MarkCurrent(node1);
        theNodes.Add(node2);
        theNodes.Add(node3);
        theNodes.Add(node4);
        
        theNodes.Publish(new AgentStarted(node1.Id, new Uri("blue://1")));
        theNodes.Publish(new AgentStarted(node1.Id, new Uri("blue://2")));
        theNodes.Publish(new AgentStarted(node2.Id, new Uri("blue://3")));

        await theTracker.DrainAsync();
        
        theNodes.FindOwnerOfAgent(new Uri("blue://1")).ShouldBe(node1);
        theNodes.FindOwnerOfAgent(new Uri("blue://2")).ShouldBe(node1);
        theNodes.FindOwnerOfAgent(new Uri("blue://3")).ShouldBe(node2);
        
        node1.ActiveAgents.ShouldHaveTheSameElementsAs(new Uri("blue://1"), new Uri("blue://2"));
    }

    [Fact]
    public async Task agent_stopped()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();
        var node4 = createNode();

        theNodes.MarkCurrent(node1);
        theNodes.Add(node2);
        theNodes.Add(node3);
        theNodes.Add(node4);
        
        theNodes.Publish(new AgentStarted(node1.Id, new Uri("blue://1")));
        theNodes.Publish(new AgentStarted(node1.Id, new Uri("blue://2")));
        theNodes.Publish(new AgentStarted(node2.Id, new Uri("blue://3")));
        
        theNodes.Publish(new AgentStopped(new Uri("blue://2")));

        await theTracker.DrainAsync();
        
        theNodes.FindOwnerOfAgent(new Uri("blue://1")).ShouldBe(node1);
        theNodes.FindOwnerOfAgent(new Uri("blue://2")).ShouldBeNull();
        theNodes.FindOwnerOfAgent(new Uri("blue://3")).ShouldBe(node2);
        
        node1.ActiveAgents.ShouldHaveTheSameElementsAs(new Uri("blue://1"));
    }
    
    [Fact]
    public async Task agent_started_that_moves_the_agent_around()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();
        var node4 = createNode();

        theNodes.MarkCurrent(node1);
        theNodes.Add(node2);
        theNodes.Add(node3);
        theNodes.Add(node4);
        
        theNodes.Publish(new AgentStarted(node1.Id, new Uri("blue://1")));
        theNodes.Publish(new AgentStarted(node1.Id, new Uri("blue://2")));
        theNodes.Publish(new AgentStarted(node2.Id, new Uri("blue://3")));
        
        theNodes.Publish(new AgentStarted(node3.Id,new Uri("blue://2")));

        await theTracker.DrainAsync();
        
        theNodes.FindOwnerOfAgent(new Uri("blue://1")).ShouldBe(node1);
        theNodes.FindOwnerOfAgent(new Uri("blue://2")).ShouldBe(node3);
        theNodes.FindOwnerOfAgent(new Uri("blue://3")).ShouldBe(node2);
        
        node1.ActiveAgents.ShouldHaveTheSameElementsAs(new Uri("blue://1"));
        node3.ActiveAgents.ShouldHaveTheSameElementsAs(new Uri("blue://2"));
    }

}