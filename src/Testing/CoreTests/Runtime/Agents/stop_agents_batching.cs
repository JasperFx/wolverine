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
/// GH-4718. Stops were the last agent command still built as one mega-batch. GH-3604 chunked the starts by
/// AgentStartBatchSize and GH-3749 chunked the reassignments, but batchCommands took every StopRemoteAgent
/// aimed at a destination and rolled it into a single StopRemoteAgents carrying the whole set.
///
/// In the field: a sharded multi-tenant cluster (512 databases, ~2,200 tenants, 37,000-58,000 agents) where
/// a node shedding its agents produced one command of 5.4-6.7 MB. The destination refuses to read anything
/// past Options.MaxIncomingEnvelopeDataSize (4 MiB by default) at all, so the chunk never arrived, and
/// AgentBatchTimeouts.ReplyWindowFor then had the leader wait it out -- 63 hours for 7,592 agents. The
/// operator's tell was that lowering AgentStartBatchSize changed nothing, because that knob had never
/// touched this path.
/// </summary>
public class stop_agents_batching
{
    private static NodeDestination destination(string name) => new(Guid.NewGuid(), new Uri($"fake://{name}"));

    private static Uri[] agents(params string[] names) => names.Select(x => new Uri($"fake://{x}")).ToArray();

    /// <summary>
    /// Every agent in the family is running on the single node and then paused, so the evaluation has to
    /// stop all of them -- the scale-down / rebalance-away shape that produced the oversized command.
    /// </summary>
    private static async Task<IReadOnlyList<IAgentCommand>> evaluatePausingEverything(int agentCount,
        int batchSize)
    {
        var options = new WolverineOptions { ApplicationAssembly = typeof(stop_agents_batching).Assembly };
        options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        options.Durability.DurabilityAgentEnabled = false;
        options.Durability.AgentStartBatchSize = batchSize;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        var family = new FakeAgentFamily("fake", agentCount);
        var controller = new NodeAgentController(
            runtime, Substitute.For<INodeAgentPersistence>(), [family],
            NullLogger<NodeAgentController>.Instance, CancellationToken.None);

        var node = new WolverineNode
        {
            NodeId = options.UniqueNodeId, AssignedNodeNumber = 1, ControlUri = new Uri("fake://self")
        };
        node.Capabilities.AddRange(family.AllAgentUris());
        node.ActiveAgents.AddRange(family.AllAgentUris());

        var restrictions = new AgentRestrictions();
        foreach (var uri in family.AllAgentUris())
        {
            restrictions.PauseAgent(uri);
        }

        return await controller.EvaluateAssignmentsAsync([node], restrictions);
    }

    [Fact]
    public async Task a_mass_stop_is_chunked_by_agent_start_batch_size()
    {
        var commands = await evaluatePausingEverything(agentCount: 50, batchSize: 7);

        // Never a pile of individual commands AgentStartBatchSize cannot touch...
        commands.OfType<StopRemoteAgent>().ShouldBeEmpty();

        var batches = commands.OfType<StopRemoteAgents>().ToArray();
        batches.ShouldNotBeEmpty();

        // ...and never one mega-batch either. THE GH-4718 defect: this used to be a single command of 50.
        batches.All(x => x.AgentIds.Length <= 7).ShouldBeTrue();

        var stopped = batches.SelectMany(x => x.AgentIds).ToArray();
        stopped.Length.ShouldBe(50);
        stopped.Distinct().Count().ShouldBe(50);
    }

    [Fact]
    public async Task the_reply_window_of_a_chunk_is_bounded_by_the_batch_size()
    {
        var commands = await evaluatePausingEverything(agentCount: 50, batchSize: 7);

        // The point of chunking is as much about the window as the bytes: an unchunked stop of 7,592 agents
        // asked the leader to wait AgentBatchTimeouts.ReplyWindowFor(7592) -- 63 hours -- before it could
        // re-decide anything at all.
        foreach (var batch in commands.OfType<StopRemoteAgents>())
        {
            AgentBatchTimeouts.ReplyWindowFor(batch.AgentIds.Length)
                .ShouldBeLessThanOrEqualTo(AgentBatchTimeouts.ReplyWindowFor(7));
        }
    }

    [Fact]
    public async Task a_stop_small_enough_to_fit_one_chunk_is_still_a_single_batch()
    {
        var commands = await evaluatePausingEverything(agentCount: 5, batchSize: 50);

        commands.OfType<StopRemoteAgents>().ShouldHaveSingleItem()
            .AgentIds.Length.ShouldBe(5);
    }

    [Fact]
    public async Task a_single_stop_is_left_as_an_unbatched_command()
    {
        // batchCommands only folds a destination with MORE than one stop. One agent stays a StopRemoteAgent,
        // which is what the pending-assignment bookkeeping in EvaluateAssignments matches on.
        var commands = await evaluatePausingEverything(agentCount: 1, batchSize: 50);

        commands.OfType<StopRemoteAgents>().ShouldBeEmpty();
        commands.OfType<StopRemoteAgent>().ShouldHaveSingleItem();
    }

    /*** VALUE EQUALITY — lets the dispatcher recognise re-emitted work; see AgentUriSet ***/

    [Fact]
    public void equality_is_order_independent_over_the_agents()
    {
        var target = destination("target");
        var one = new StopRemoteAgents(target, agents("a", "b", "c"));

        one.ShouldBe(new StopRemoteAgents(target, agents("c", "a", "b")));
        one.GetHashCode().ShouldBe(new StopRemoteAgents(target, agents("c", "a", "b")).GetHashCode());
    }
}
