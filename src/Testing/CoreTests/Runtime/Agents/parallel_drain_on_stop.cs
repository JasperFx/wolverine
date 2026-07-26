using CoreTests.Transports;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Regression coverage for D3/WO-7 of the projection-agent churn livelock (GH-3604). Node shutdown drained
/// every locally-running agent SERIALLY (the old foreach in stopAllAgentsAsync), which could not finish
/// thousands of daemon subscription agents inside a typical 30s SIGTERM grace window -- k8s then SIGKILLed
/// the process mid-drain and abandoned unflushed daemon progression. WO-7 drains with bounded parallelism
/// (Durability.MaxAgentStopParallelism) so the shutdown window is usable at scale.
/// </summary>
public class parallel_drain_on_stop
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;

    public parallel_drain_on_stop()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());
    }

    private NodeAgentController buildController()
    {
        return new NodeAgentController(
            _runtime, Substitute.For<INodeAgentPersistence>(),
            [], NullLogger<NodeAgentController>.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task drains_running_agents_with_bounded_parallelism()
    {
        const int dop = 4;
        const int total = 12;
        _options.Durability.MaxAgentStopParallelism = dop;

        var gate = new object();
        var concurrent = 0;
        var maxConcurrent = 0;
        var reachedCap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var controller = buildController();
        for (var i = 0; i < total; i++)
        {
            var uri = new Uri($"fake://{i}");
            controller.Agents[uri] = new GatedAgent(uri, async () =>
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
        }

        var stopping = controller.StopAsync(Substitute.For<IMessageBus>());

        // Exactly `dop` drains run concurrently; the rest queue behind them (Parallel.ForEachAsync bound).
        await reachedCap.Task.WaitAsync(5.Seconds());
        lock (gate)
        {
            concurrent.ShouldBe(dop);
            maxConcurrent.ShouldBe(dop);
        }

        release.SetResult();
        await stopping.WaitAsync(5.Seconds());

        // Every agent was stopped and removed, and parallelism never exceeded the cap.
        maxConcurrent.ShouldBe(dop);
        controller.Agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_wedged_agent_does_not_abort_the_drain_of_its_peers()
    {
        _options.Durability.MaxAgentStopParallelism = 4;

        var controller = buildController();

        var good = new List<GatedAgent>();
        for (var i = 0; i < 6; i++)
        {
            var uri = new Uri($"fake://good/{i}");
            var agent = new GatedAgent(uri, () => Task.CompletedTask);
            good.Add(agent);
            controller.Agents[uri] = agent;
        }

        var badUri = new Uri("fake://bad");
        controller.Agents[badUri] = new GatedAgent(badUri, () => throw new InvalidOperationException("wedged"));

        await controller.StopAsync(Substitute.For<IMessageBus>()).WaitAsync(5.Seconds());

        // Every healthy agent still drained and was removed even though a peer threw. The throwing agent is
        // logged and left in the map (only a clean stop removes the entry), so the failure is contained.
        good.All(x => x.Status == AgentStatus.Stopped).ShouldBeTrue();
        good.All(x => !controller.Agents.ContainsKey(x.Uri)).ShouldBeTrue();
        controller.Agents.Keys.ShouldBe([badUri]);
    }

    private class GatedAgent : IAgent
    {
        private readonly Func<Task> _onStop;

        public GatedAgent(Uri uri, Func<Task> onStop)
        {
            Uri = uri;
            _onStop = onStop;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Status = AgentStatus.Running;
            await Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _onStop();
            Status = AgentStatus.Stopped;
        }

        public AgentStatus Status { get; private set; } = AgentStatus.Running;
        public Uri Uri { get; }
    }
}
