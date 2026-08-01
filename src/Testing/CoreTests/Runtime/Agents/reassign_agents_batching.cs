using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3749. Reassignment was the one agent command that broke both halves of the dispatcher's contract: it
/// ran in the DESTINATION node's lane while doing its blocking stop round trip against the SOURCE — so a
/// source that was merely busy froze every batched start queued for a healthy destination — and it was the
/// one command type batchCommands never chunked, so a rebalance emitted thousands of serial single-agent
/// round trips that AgentStartBatchSize had no effect on. In the field: a stable five-node cluster frozen
/// for seven minutes, one node holding 799 agents and four holding zero, because 22 stops aimed at a busy
/// source sat at the head of an idle destination's lane.
/// </summary>
public class reassign_agents_batching
{
    private static NodeDestination destination(string name) => new(Guid.NewGuid(), new Uri($"fake://{name}"));

    private static Uri[] agents(params string[] names) => names.Select(x => new Uri($"fake://{x}")).ToArray();

    /*** LANE KEYING ***/

    [Fact]
    public void single_reassignment_runs_in_the_source_nodes_lane()
    {
        var source = destination("source");
        var target = destination("target");

        // THE GH-3749 defect: this used to be ActiveNode.NodeId, putting a blocking round trip against the
        // source into the destination's lane, where it wedged the batched starts behind it.
        new ReassignAgent(new Uri("fake://one"), source, target)
            .DestinationNodeId.ShouldBe(source.NodeId);
    }

    [Fact]
    public void batched_reassignment_runs_in_the_source_nodes_lane()
    {
        var source = destination("source");
        var target = destination("target");

        new ReassignAgents(source, target, agents("one", "two"))
            .DestinationNodeId.ShouldBe(source.NodeId);
    }

    /*** EXECUTION ***/

    private readonly MockWolverineRuntime theRuntime = new();
    private readonly NodeDestination theSource = destination("source");
    private readonly NodeDestination theTarget = destination("target");

    private void sourceConfirmsStopping(params string[] names)
    {
        theRuntime.Agents
            .InvokeAsync<AgentsStopped>(theSource, Arg.Any<IAgentCommand>(), Arg.Any<TimeSpan?>())
            .Returns(new AgentsStopped(agents(names)));
    }

    [Fact]
    public async Task stops_the_batch_on_the_source_with_a_reply_window_scaled_to_the_batch_size()
    {
        sourceConfirmsStopping("one", "two", "three");

        var command = new ReassignAgents(theSource, theTarget, agents("one", "two", "three"));
        await command.ExecuteAsync(theRuntime, CancellationToken.None);

        await theRuntime.Agents.Received().InvokeAsync<AgentsStopped>(
            theSource,
            new StopAgents(agents("one", "two", "three")),
            AgentBatchTimeouts.ReplyWindowFor(3));
    }

    [Fact]
    public async Task cascades_one_batched_start_into_the_destinations_lane_when_every_stop_is_confirmed()
    {
        sourceConfirmsStopping("one", "two", "three");

        var command = new ReassignAgents(theSource, theTarget, agents("one", "two", "three"));
        var cascaded = await command.ExecuteAsync(theRuntime, CancellationToken.None);

        cascaded.ShouldHaveSingleItem()
            .ShouldBe(new AssignAgents(theTarget, agents("one", "two", "three")));
    }

    [Fact]
    public async Task only_confirmed_stops_are_followed_by_starts()
    {
        // The source let go of two of three inside the reply window. Starting "three" anywhere else while
        // its old copy may still be running is the two-copies bug, so it stays put for the next evaluation.
        sourceConfirmsStopping("one", "two");

        var command = new ReassignAgents(theSource, theTarget, agents("one", "two", "three"));
        var cascaded = await command.ExecuteAsync(theRuntime, CancellationToken.None);

        cascaded.ShouldHaveSingleItem()
            .ShouldBe(new AssignAgents(theTarget, agents("one", "two")));
    }

    [Fact]
    public async Task no_starts_at_all_when_nothing_was_confirmed_stopped()
    {
        sourceConfirmsStopping();

        var command = new ReassignAgents(theSource, theTarget, agents("one", "two"));
        var cascaded = await command.ExecuteAsync(theRuntime, CancellationToken.None);

        cascaded.ShouldBeEmpty();
    }

    [Fact]
    public async Task no_starts_when_the_reply_never_came()
    {
        // The NSubstitute default — no reply within the window resolves to null rather than throwing here.
        var command = new ReassignAgents(theSource, theTarget, agents("one", "two"));
        var cascaded = await command.ExecuteAsync(theRuntime, CancellationToken.None);

        cascaded.ShouldBeEmpty();
    }

    /*** VALUE EQUALITY — lets the dispatcher recognise re-emitted work; see AgentUriSet ***/

    [Fact]
    public void equality_is_order_independent_over_the_agents()
    {
        var one = new ReassignAgents(theSource, theTarget, agents("a", "b", "c"));

        one.ShouldBe(new ReassignAgents(theSource, theTarget, agents("c", "a", "b")));
        one.GetHashCode().ShouldBe(new ReassignAgents(theSource, theTarget, agents("c", "a", "b")).GetHashCode());
    }

    [Fact]
    public void different_nodes_or_agents_are_different_commands()
    {
        var one = new ReassignAgents(theSource, theTarget, agents("a", "b"));

        one.ShouldNotBe(new ReassignAgents(theSource, theTarget, agents("a", "c")));
        one.ShouldNotBe(new ReassignAgents(destination("other"), theTarget, agents("a", "b")));
        one.ShouldNotBe(new ReassignAgents(theSource, destination("other"), agents("a", "b")));
    }

    /*** BATCHING IN THE LEADER'S EVALUATION ***/

    [Fact]
    public async Task a_rebalance_chunks_reassignments_by_agent_start_batch_size()
    {
        var options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        options.Durability.DurabilityAgentEnabled = false;
        options.Durability.AgentStartBatchSize = 2;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        var family = new FakeAgentFamily("fake"); // 12 known agents
        var controller = new NodeAgentController(
            runtime, Substitute.For<INodeAgentPersistence>(), [family],
            NullLogger<NodeAgentController>.Instance, CancellationToken.None);

        // Node 1 is running everything; node 2 joins empty. DistributeEvenly moves half across — the
        // rebalance-after-a-deploy shape from GH-3753.
        var node1 = new WolverineNode
        {
            NodeId = options.UniqueNodeId, AssignedNodeNumber = 1, ControlUri = new Uri("fake://self")
        };
        node1.Capabilities.AddRange(family.AllAgentUris());
        node1.ActiveAgents.AddRange(family.AllAgentUris());

        var node2 = new WolverineNode
        {
            NodeId = Guid.NewGuid(), AssignedNodeNumber = 2, ControlUri = new Uri("fake://two")
        };
        node2.Capabilities.AddRange(family.AllAgentUris());

        var commands = await controller.EvaluateAssignmentsAsync([node1, node2], new AgentRestrictions());

        // Every moved agent travels as a chunked ReassignAgents — never as a pile of individual commands
        // AgentStartBatchSize cannot touch.
        commands.OfType<ReassignAgent>().ShouldBeEmpty();

        var batches = commands.OfType<ReassignAgents>().ToArray();
        batches.ShouldNotBeEmpty();
        batches.All(x => x.AgentUris.Length <= 2).ShouldBeTrue();
        batches.All(x => x.OriginalNode.NodeId == node1.NodeId && x.ActiveNode.NodeId == node2.NodeId)
            .ShouldBeTrue();

        // Half of twelve agents move, each exactly once.
        var moved = batches.SelectMany(x => x.AgentUris).ToArray();
        moved.Length.ShouldBe(6);
        moved.Distinct().Count().ShouldBe(6);
    }

    /*** THE GH-3748 REPLY WINDOW ***/

    [Fact]
    public void small_batches_keep_the_second_per_agent_window()
    {
        AgentBatchTimeouts.ReplyWindowFor(1).ShouldBe(31.Seconds());
        AgentBatchTimeouts.ReplyWindowFor(20).ShouldBe(50.Seconds());
    }

    [Fact]
    public void batches_past_the_threshold_get_thirty_seconds_per_agent()
    {
        // GH-3748: a projection shard behind a version bump was measured at 27s p50 / 215s max per start,
        // so the old 30s + N window could never be satisfied at any batch size.
        AgentBatchTimeouts.ReplyWindowFor(21).ShouldBe(660.Seconds());
        AgentBatchTimeouts.ReplyWindowFor(100).ShouldBe(3030.Seconds());
    }
}
