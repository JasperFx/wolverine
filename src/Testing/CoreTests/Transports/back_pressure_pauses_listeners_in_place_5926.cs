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
/// GH-5926. Back pressure stops and disposes a listener whose endpoint holds too many messages, and builds a new one
/// once the backlog drains. A listener that settles each delivery only once it finishes (a buffered Kafka listener)
/// would hand its whole backlog back to the broker that way, so it can say it pauses in place instead. Only the back
/// pressure latch uses that. Every real stop still stops and disposes the listener.
/// </summary>
public class back_pressure_pauses_listeners_in_place_5926 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<(ListeningAgent, List<RecordingPausableListener>)> startAsync(string name, bool pausesInPlace)
    {
        var endpoint = new PausableListenerEndpoint(name, pausesInPlace) { IsListener = true };
        endpoint.Mode = EndpointMode.BufferedInMemory;
        endpoint.Compile(theRuntime);

        var agent = new ListeningAgent(endpoint, theRuntime);
        await agent.StartAsync();

        return (agent, endpoint.Built);
    }

    [Fact]
    public async Task a_listener_that_pauses_in_place_is_paused_and_resumed_not_rebuilt()
    {
        var (agent, built) = await startAsync("pause-in-place-5926", pausesInPlace: true);
        await using var _ = agent;
        var listener = built.Single();

        await agent.MarkAsTooBusyAndStopReceivingAsync();

        agent.Status.ShouldBe(ListeningStatus.TooBusy);
        agent.Listener.ShouldBeSameAs(listener);
        listener.Paused.ShouldBe(1);
        listener.Stopped.ShouldBe(0);
        listener.Disposed.ShouldBe(0);

        (await agent.ResumeFromBackPressureAsync()).ShouldBeTrue();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        agent.Listener.ShouldBeSameAs(listener);
        listener.Resumed.ShouldBe(1);
        listener.Stopped.ShouldBe(0);
        built.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_real_stop_while_paused_still_stops_and_disposes_the_listener()
    {
        var (agent, built) = await startAsync("pause-then-stop-5926", pausesInPlace: true);
        await using var _ = agent;
        var listener = built.Single();

        await agent.MarkAsTooBusyAndStopReceivingAsync();
        await agent.StopAndDrainAsync();

        agent.Status.ShouldBe(ListeningStatus.Stopped);
        listener.Stopped.ShouldBeGreaterThanOrEqualTo(1);
        listener.Disposed.ShouldBeGreaterThanOrEqualTo(1);
        listener.Resumed.ShouldBe(0);

        // A start after the stop builds a new listener rather than resuming the stopped one
        await agent.StartAsync();
        built.Count.ShouldBe(2);
        listener.Resumed.ShouldBe(0);
    }

    [Fact]
    public async Task a_start_while_paused_resumes_the_paused_listener_rather_than_building_another()
    {
        var (agent, built) = await startAsync("pause-then-start-5926", pausesInPlace: true);
        await using var _ = agent;
        var listener = built.Single();

        await agent.MarkAsTooBusyAndStopReceivingAsync();
        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        agent.Listener.ShouldBeSameAs(listener);
        listener.Resumed.ShouldBe(1);
        built.Count.ShouldBe(1);
    }

    [Fact]
    public async Task any_other_listener_is_still_stopped_and_rebuilt()
    {
        var (agent, built) = await startAsync("stop-and-rebuild-5926", pausesInPlace: false);
        await using var _ = agent;
        var listener = built.Single();

        await agent.MarkAsTooBusyAndStopReceivingAsync();

        agent.Status.ShouldBe(ListeningStatus.TooBusy);
        agent.Listener.ShouldBeNull();
        listener.Paused.ShouldBe(0);
        listener.Stopped.ShouldBe(1);
        listener.Disposed.ShouldBe(1);

        (await agent.ResumeFromBackPressureAsync()).ShouldBeTrue();

        built.Count.ShouldBe(2);
        agent.Listener.ShouldBeSameAs(built[1]);
    }

    private class PausableListenerEndpoint : Wolverine.Transports.Stub.StubEndpoint
    {
        private readonly bool _pausesInPlace;

        public PausableListenerEndpoint(string name, bool pausesInPlace) : base(name, new StubTransport())
        {
            _pausesInPlace = pausesInPlace;
        }

        public List<RecordingPausableListener> Built { get; } = new();

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            var listener = new RecordingPausableListener(Uri, _pausesInPlace);
            Built.Add(listener);
            return ValueTask.FromResult<IListener>(listener);
        }
    }

    private class RecordingPausableListener : IPausesForBackPressure
    {
        public RecordingPausableListener(Uri address, bool pausesInPlace)
        {
            Address = address;
            PausesForBackPressure = pausesInPlace;
        }

        public int Paused { get; private set; }
        public int Resumed { get; private set; }
        public int Stopped { get; private set; }
        public int Disposed { get; private set; }

        public bool PausesForBackPressure { get; }

        public ValueTask PauseForBackPressureAsync()
        {
            Paused++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAfterBackPressureAsync()
        {
            Resumed++;
            return ValueTask.CompletedTask;
        }

        public Uri Address { get; }
        public IHandlerPipeline? Pipeline => null;
        public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

        public ValueTask StopAsync()
        {
            Stopped++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }
}
