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
/// Regression coverage for D3 of the projection-agent churn livelock (GH-3604). Assignments to a node used
/// to go out as one mega-batch (~2,100 URIs) answered by a fixed 30s reply window, and the receiving node
/// started them SERIALLY -- hours of work the reply could never wait out, so the leader re-sent the whole
/// batch every cycle. WO-4 chunks a destination's assignments into AgentStartBatchSize-sized batches, starts
/// each batch with bounded parallelism (MaxAgentStartParallelism) on the receiving node, and scales the
/// reply timeout with chunk size.
/// </summary>
public class scale_safe_batch_starts
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;

    public scale_safe_batch_starts()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());
    }

    [Fact]
    public async Task chunks_a_destinations_assignments_into_configured_batch_sizes()
    {
        _options.Durability.AgentStartBatchSize = 5;

        var family = new FakeAgentFamily("fake"); // 12 known agents
        var controller = new NodeAgentController(
            _runtime, Substitute.For<INodeAgentPersistence>(), [family],
            NullLogger<NodeAgentController>.Instance, CancellationToken.None);

        var self = new WolverineNode
        {
            NodeId = _options.UniqueNodeId,
            AssignedNodeNumber = 1,
            ControlUri = _options.Transports.NodeControlEndpoint!.Uri
        };
        self.Capabilities.AddRange(family.AllAgentUris());

        var commands = await controller.EvaluateAssignmentsAsync([self], new AgentRestrictions());

        // 12 agents, batch size 5 -> 3 chunks of at most 5, covering all 12 exactly once.
        var batches = commands.OfType<AssignAgents>().ToArray();
        batches.Length.ShouldBe(3);
        batches.All(x => x.AgentIds.Length <= 5).ShouldBeTrue();
        batches.SelectMany(x => x.AgentIds).Distinct().Count().ShouldBe(FakeAgentFamily.Names.Length);
    }

    [Fact]
    public async Task starts_a_batch_with_bounded_parallelism()
    {
        const int dop = 4;
        const int total = 8;
        _options.Durability.MaxAgentStartParallelism = dop;

        var gate = new object();
        var concurrent = 0;
        var maxConcurrent = 0;
        var reachedCap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var agents = Substitute.For<IAgentRuntime>();
        agents.StartLocallyAsync(Arg.Any<Uri>()).Returns(async _ =>
        {
            int now;
            lock (gate)
            {
                concurrent++;
                now = concurrent;
                if (concurrent > maxConcurrent) maxConcurrent = concurrent;
            }

            if (now >= dop) reachedCap.TrySetResult();

            await release.Task;

            lock (gate) { concurrent--; }
        });
        _runtime.Agents.Returns(agents);
        _runtime.Logger.Returns(NullLogger.Instance);

        var uris = Enumerable.Range(0, total).Select(i => new Uri($"fake://{i}")).ToArray();
        var exec = new StartAgents(uris).ExecuteAsync(_runtime, CancellationToken.None);

        // Exactly `dop` starts run concurrently; the rest queue behind them (Parallel.ForEachAsync bound).
        await reachedCap.Task.WaitAsync(5.Seconds());
        lock (gate)
        {
            concurrent.ShouldBe(dop);
            maxConcurrent.ShouldBe(dop);
        }

        release.SetResult();
        var result = await exec.WaitAsync(5.Seconds());

        // Every agent eventually started, and parallelism never exceeded the cap.
        result.OfType<AgentsStarted>().Single().AgentUris.Length.ShouldBe(total);
        maxConcurrent.ShouldBe(dop);
    }
}
