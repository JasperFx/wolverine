using CoreTests.Transports;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TestingSupport;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;


public class starting_nodes_as_the_first_node : NodeAgentControllerTestsContext, IAsyncLifetime
{
    public override Task InitializeAsync()
    {
        theControllers.Add(new FakeAgentFamily("blue"));
        return afterStarting();
    }

    [Fact]
    public void tracker_should_have_the_current_node()
    {
        theNodes.Self.Id.ShouldBe(theOptions.UniqueNodeId);
    }

    [Fact]
    public void should_record_the_capabilities_on_the_tracked_node_in_memory()
    {
        theNodes.Self.Capabilities.ShouldContain(new Uri("blue://one"));
        theNodes.Self.Capabilities.ShouldContain(new Uri("blue://six"));
        theNodes.Self.Capabilities.ShouldContain(new Uri("blue://seven"));
    }

    [Fact]
    public async Task should_have_persisted_the_current_node()
    {
        await thePersistence.Received().PersistAsync(theNodes.Self, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void should_have_cascaded_an_attempt_to_assume_leadership()
    {
        theCascadedMessages.OfType<TryAssumeLeadership>().Single()
            .CurrentLeaderId.ShouldBeNull();
    }
}

public class starting_a_second_node_with_an_existing_leader : NodeAgentControllerTestsContext
{
    private WolverineNode node1 = new WolverineNode
    {
        AssignedNodeId = 1,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://1"),
        ActiveAgents = { NodeAgentController.LeaderUri }
    };

    private WolverineNode node2 = new WolverineNode
    {
        AssignedNodeId = 2,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://2"),
    };

    private WolverineNode node3 = new WolverineNode
    {
        AssignedNodeId = 3,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://3"),
    };

    public override async Task InitializeAsync()
    {
        theOtherNodes.Add(node1);
        theOtherNodes.Add(node2);
        theOtherNodes.Add(node3);

        theControllers.Add(new FakeAgentFamily("blue"));
        await afterStarting();
    }

    [Fact]
    public void tracker_should_have_the_current_node()
    {
        theNodes.Self.Id.ShouldBe(theOptions.UniqueNodeId);
    }

    [Fact]
    public void should_record_the_capabilities_on_the_tracked_node_in_memory()
    {
        theNodes.Self.Capabilities.ShouldContain(new Uri("blue://one"));
        theNodes.Self.Capabilities.ShouldContain(new Uri("blue://six"));
        theNodes.Self.Capabilities.ShouldContain(new Uri("blue://seven"));
    }

    [Fact]
    public async Task should_have_persisted_the_current_node()
    {
        await thePersistence.Received().PersistAsync(theNodes.Self, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void should_have_attempted_any_leadership_election()
    {
        theCascadedMessages.OfType<TryAssumeLeadership>().Any().ShouldBeFalse();
    }
}

public class starting_a_subsequent_node_with_no_existing_leader : NodeAgentControllerTestsContext
{
    private WolverineNode node1 = new WolverineNode
    {
        AssignedNodeId = 1,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://1")
    };

    private WolverineNode node2 = new WolverineNode
    {
        AssignedNodeId = 2,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://2")
    };

    private WolverineNode node3 = new WolverineNode
    {
        AssignedNodeId = 3,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://3"),
    };

    public override async Task InitializeAsync()
    {
        theOtherNodes.Add(node1);
        theOtherNodes.Add(node2);
        theOtherNodes.Add(node3);

        theControllers.Add(new FakeAgentFamily("blue"));
        await afterStarting();
    }

    [Fact]
    public void tracker_should_have_the_current_node()
    {
        theNodes.Self.Id.ShouldBe(theOptions.UniqueNodeId);
    }

    [Fact]
    public void should_record_the_capabilities_on_the_tracked_node_in_memory()
    {
        theNodes.Self.Capabilities.ShouldContain(new Uri("blue://one"));
        theNodes.Self.Capabilities.ShouldContain(new Uri("blue://six"));
        theNodes.Self.Capabilities.ShouldContain(new Uri("blue://seven"));
    }

    [Fact]
    public async Task should_have_persisted_the_current_node()
    {
        await thePersistence.Received().PersistAsync(theNodes.Self, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void should_have_asked_the_node_with_smallest_assigned_number_to_take_leadership()
    {
        var leadership = theCascadedMessages.OfType<TryAssumeLeadership>().Single();
        leadership.CandidateId.ShouldBe(node1.Id);
        leadership.ShouldNotBeNull();
    }
}

public class try_assume_leadership_from_scratch_happy_path : NodeAgentControllerTestsContext
{
    private WolverineNode node1 = new WolverineNode
    {
        AssignedNodeId = 1,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://1"),
        ActiveAgents = { NodeAgentController.LeaderUri }
    };

    private WolverineNode node2 = new WolverineNode
    {
        AssignedNodeId = 2,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://2"),
    };

    private WolverineNode node3 = new WolverineNode
    {
        AssignedNodeId = 3,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://3"),
    };

    public override async Task InitializeAsync()
    {
        theOtherNodes.Add(node1);
        theOtherNodes.Add(node2);
        theOtherNodes.Add(node3);

        foreach (var node in theOtherNodes)
        {
            theNodes.Add(node);
        }

        thePersistence.MarkNodeAsLeaderAsync(null, theOptions.UniqueNodeId).Returns(theOptions.UniqueNodeId);

        theNodes.MarkCurrent(WolverineNode.For(theOptions));

        theCascadedMessages = await theController.AssumeLeadershipAsync(null);

        await theTracker.DrainAsync();
    }

    [Fact]
    public void should_publish_event_to_itself_on_taking_leadership()
    {
        thePublishedEvents.OfType<NodeEvent>().ShouldContain(new NodeEvent(theNodes.Self, NodeEventType.LeadershipAssumed));
    }

    [Fact]
    public void should_have_sent_a_leadership_assumed_message_to_each_node()
    {
        var nodeMessages = theCascadedMessages.OfType<RemoteNodeEvent>().ToArray();
        nodeMessages.Length.ShouldBe(3);
        foreach (var nodeEvent in nodeMessages)
        {
            nodeEvent.Type.ShouldBe(NodeEventType.LeadershipAssumed);
        }
    }

    [Fact]
    public void should_not_be_triggering_second_attempt_to_try_leadership()
    {
        theCascadedMessages.OfType<TryAssumeLeadership>().Any().ShouldBeFalse();
    }
}

public class try_assume_leadership_from_scratch_nothing_is_assigned : NodeAgentControllerTestsContext
{
    private WolverineNode node1 = new WolverineNode
    {
        AssignedNodeId = 1,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://1"),
        ActiveAgents = { NodeAgentController.LeaderUri }
    };

    private WolverineNode node2 = new WolverineNode
    {
        AssignedNodeId = 2,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://2"),
    };

    private WolverineNode node3 = new WolverineNode
    {
        AssignedNodeId = 3,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://3"),
    };

    public override async Task InitializeAsync()
    {
        theOtherNodes.Add(node1);
        theOtherNodes.Add(node2);
        theOtherNodes.Add(node3);

        foreach (var node in theOtherNodes)
        {
            theNodes.Add(node);
        }

        thePersistence.MarkNodeAsLeaderAsync(null, theOptions.UniqueNodeId).Returns((Guid?)null);

        theNodes.MarkCurrent(WolverineNode.For(theOptions));

        theCascadedMessages = await theController.AssumeLeadershipAsync(null);

        await theTracker.DrainAsync();
    }

    [Fact]
    public void should_be_no_published_tracker_events()
    {
        thePublishedEvents.Any().ShouldBeFalse();
    }

    [Fact]
    public void should_republish_try_leadership_for_another_attempt()
    {
        theCascadedMessages.Single().ShouldBeOfType<TryAssumeLeadership>();
    }
}

public class handling_node_events : NodeAgentControllerTestsContext
{
    private WolverineNode node1 = new WolverineNode
    {
        AssignedNodeId = 1,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://1"),
        ActiveAgents = { NodeAgentController.LeaderUri }
    };

    private WolverineNode node2 = new WolverineNode
    {
        AssignedNodeId = 2,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://2"),
    };

    private WolverineNode node3 = new WolverineNode
    {
        AssignedNodeId = 3,
        Id = Guid.NewGuid(),
        ControlUri = new Uri("fake://3"),
    };

    public override Task InitializeAsync()
    {
        theNodes.MarkCurrent(WolverineNode.For(theOptions));

        theOtherNodes.Add(node1);
        theOtherNodes.Add(node2);
        theOtherNodes.Add(node3);

        foreach (var node in theOtherNodes)
        {
            theNodes.Add(node);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task get_a_new_node_not_the_leader()
    {
        var node = new WolverineNode
        {
            Id = Guid.NewGuid(),
            AssignedNodeId = 5,
            ControlUri = new Uri("fake://5"),
        };

        var e = new NodeEvent(node, NodeEventType.Started);
        await afterHandlingEvent(e);


        thePublishedEvents.Single().ShouldBe(e);

        theCascadedMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task get_node_exiting_when_not_the_leader_and_that_node_is_not_leader()
    {
        var e = new NodeEvent(node3, NodeEventType.Exiting);
        await afterHandlingEvent(e);

        // remove the node from memory
        theNodes.OtherNodes().ShouldNotContain(node3);

        thePublishedEvents.All(x => x.Equals(e));

        theCascadedMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task get_node_exiting_when_the_leader()
    {
        new NodeEvent(theNodes.Self, NodeEventType.LeadershipAssumed).ModifyState(theTracker);

        var e = new NodeEvent(node3, NodeEventType.Exiting);
        await afterHandlingEvent(e);

        // remove the node from memory
        theNodes.OtherNodes().ShouldNotContain(node3);

        // should delete the other node just in case
        await thePersistence.Received().DeleteAsync(node3.Id);

        thePublishedEvents.ShouldContain(e);

    }

    [Fact]
    public async Task current_leader_is_exiting_and_other_nodes_should_have_precedence_to_be_leader()
    {
        // The current node will be higher than others, so won't become the leader
        theTracker.Self.AssignedNodeId = 20;

        new NodeEvent(node2, NodeEventType.LeadershipAssumed).ModifyState(theTracker);

        theNodes.Leader.ShouldBe(node2);

        var e = new NodeEvent(node2, NodeEventType.Exiting);
        await afterHandlingEvent(e);

        // remove the node from memory
        theNodes.OtherNodes().ShouldNotContain(node2);

        // should try to ask lowest node to take ownership
        var ownership = theCascadedMessages.OfType<TryAssumeLeadership>().Single();
        ownership.ShouldNotBeNull();
    }

    [Fact]
    public async Task current_leader_is_exiting_and_no_other_nodes_should_have_precedence_to_be_leader()
    {
        // The current node will be lower than others, so should try to become the new leader
        theTracker.Self.AssignedNodeId = 0;

        new NodeEvent(node2, NodeEventType.LeadershipAssumed).ModifyState(theTracker);

        theNodes.Leader.ShouldBe(node2);

        var e = new NodeEvent(node2, NodeEventType.Exiting);
        await afterHandlingEvent(e);

        // remove the node from memory
        theNodes.OtherNodes().ShouldNotContain(node2);

        // should try to ask lowest node to take ownership
        theCascadedMessages.OfType<TryAssumeLeadership>()
            .Any().ShouldBeTrue();
    }
}

public abstract class NodeAgentControllerTestsContext : IObserver<IWolverineEvent>, IAsyncLifetime
{
    protected readonly List<WolverineNode> theOtherNodes = new();

    protected readonly WolverineOptions theOptions = new();

    protected readonly INodeAgentPersistence thePersistence = Substitute.For<INodeAgentPersistence>();
    protected readonly WolverineTracker theTracker = new WolverineTracker(NullLogger.Instance);
    internal readonly INodeStateTracker theNodes;
    protected readonly List<IWolverineEvent> thePublishedEvents = new();
    protected readonly CancellationToken theCancellation = CancellationToken.None;
    protected readonly List<IAgentFamily> theControllers = new();

    protected AgentCommands theCascadedMessages;

    protected NodeAgentControllerTestsContext()
    {
        theOptions.Transports.NodeControlEndpoint = new FakeEndpoint("fake://control".ToUri(), EndpointRole.System);

        thePersistence.LoadAllNodesAsync(Arg.Any<CancellationToken>()).Returns(theOtherNodes);
        theTracker.Subscribe(this);

        theNodes = theTracker.As<INodeStateTracker>();
    }

    private NodeAgentController _controller;

    protected NodeAgentController theController
    {
        get
        {
            _controller ??= new NodeAgentController(new MockWolverineRuntime(), theTracker, thePersistence, theControllers, NullLogger.Instance,
                theCancellation);

            return _controller;
        }
    }

    public abstract Task InitializeAsync();

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public void OnCompleted()
    {

    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(IWolverineEvent value)
    {
        thePublishedEvents.Add(value);
    }

    public async Task afterStarting()
    {
        theCascadedMessages = await theController.StartLocalAgentProcessingAsync(theOptions);

        await theTracker.DrainAsync();
    }

    public async Task afterHandlingEvent(NodeEvent @event)
    {
        theCascadedMessages = await theController.ApplyNodeEventAsync(@event);

        await theTracker.DrainAsync();
    }
}