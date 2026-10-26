using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// GH-5217. ListeningAgent.StartAsync is reached from the circuit breaker Restarter, InboxHealthRestarter, back
/// pressure, agent-restriction resume and the control endpoint. It used to check for Accepting once, with no lock,
/// so two starts that overlapped both built a full listener set and the second replaced Listener. The first set was
/// never stopped, and its broker consumers stayed attached to the queue.
/// </summary>
public class concurrent_starts_build_one_listener_set_5217 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Durability.Mode = DurabilityMode.Solo)
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task two_overlapping_starts_build_exactly_one_set_of_listeners()
    {
        var endpoint = new GatedListenerEndpoint("concurrent-starts-5217")
        {
            Mode = EndpointMode.BufferedInMemory,
            ListenerCount = 2
        };
        endpoint.Compile(theRuntime);

        await using var agent = new ListeningAgent(endpoint, theRuntime);

        var first = agent.StartAsync().AsTask();

        // The first start is now parked inside BuildListenerAsync, past its Accepting check
        await endpoint.WaitForFirstBuildAsync();

        var second = agent.StartAsync().AsTask();

        // Give an unserialized second start every chance to reach BuildListenerAsync as well
        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);

        endpoint.ReleaseBuilds();
        await Task.WhenAll(first, second).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        endpoint.BuildCount.ShouldBe(endpoint.ListenerCount);
    }

    internal class GatedListenerEndpoint : Wolverine.Transports.Stub.StubEndpoint
    {
        private readonly TaskCompletionSource _firstBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _buildCount;

        public GatedListenerEndpoint(string queueName) : base(queueName, new StubTransport())
        {
        }

        public int BuildCount => Volatile.Read(ref _buildCount);

        public Task WaitForFirstBuildAsync() => _firstBuild.Task.WaitAsync(10.Seconds());

        public void ReleaseBuilds() => _gate.TrySetResult();

        public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            Interlocked.Increment(ref _buildCount);
            _firstBuild.TrySetResult();

            await _gate.Task;

            return new CountedListener(Uri);
        }
    }

    internal class CountedListener : IListener
    {
        public CountedListener(Uri address)
        {
            Address = address;
        }

        public Uri Address { get; }
        public IHandlerPipeline? Pipeline => null;

        public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask StopAsync() => ValueTask.CompletedTask;
    }
}
