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
/// GH-5934. ListeningAgent stops a listener, drains the receiver, then disposes the listener. The Kafka listener now
/// keeps its consumer open through the drain and only closes it in DisposeAsync, so a stop that fails part way through
/// (an inline drain that times out throws, for one) must still dispose the listener rather than leave the connection
/// open. It used to skip the dispose entirely.
/// </summary>
public class a_failed_stop_still_disposes_the_listener_5934 : IAsyncLifetime
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

    [Fact]
    public async Task a_listener_whose_stop_throws_is_still_disposed()
    {
        var endpoint = new FailingStopEndpoint("failed-stop-5934") { IsListener = true };
        endpoint.Mode = EndpointMode.BufferedInMemory;
        endpoint.Compile(theRuntime);

        var agent = new ListeningAgent(endpoint, theRuntime);
        await using var _ = agent;
        await agent.StartAsync();

        var listener = endpoint.Built.Single();

        await agent.StopAndDrainAsync();

        agent.Status.ShouldBe(ListeningStatus.Stopped);
        listener.Stopped.ShouldBe(1);
        listener.Disposed.ShouldBe(1);
    }

    private class FailingStopEndpoint : Wolverine.Transports.Stub.StubEndpoint
    {
        public FailingStopEndpoint(string name) : base(name, new StubTransport())
        {
        }

        public List<FailingStopListener> Built { get; } = new();

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            var listener = new FailingStopListener(Uri);
            Built.Add(listener);
            return ValueTask.FromResult<IListener>(listener);
        }
    }

    private class FailingStopListener : IListener
    {
        public FailingStopListener(Uri address)
        {
            Address = address;
        }

        public int Stopped { get; private set; }
        public int Disposed { get; private set; }

        public Uri Address { get; }
        public IHandlerPipeline? Pipeline => null;
        public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

        public ValueTask StopAsync()
        {
            Stopped++;
            throw new InvalidOperationException("The stop failed");
        }

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }
}
