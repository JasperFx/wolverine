using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-5217. DrainAsync completes the receiving block, and a completed block drops whatever is posted to it. The
/// ReceivedAsync paths ack every delivery right after posting it, so a broker delivery that reached the receiver
/// after its drain -- from a consumer leaked by two racing restarts (GH-5213), or anything late -- was acked and
/// lost: no handler, no dead letter, no log line. It has to go back to the broker instead.
/// </summary>
public class a_drained_buffered_receiver_defers_late_deliveries_5217 : IAsyncLifetime
{
    private IHost _host = null!;
    private IWolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<NativeAckPingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = _host.Services.GetRequiredService<IWolverineRuntime>();
        NativeAckPingHandler.Handled.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private BufferedReceiver bufferedReceiver()
    {
        var endpoint = new StubEndpoint("buffered-drained-5217", new StubTransport())
        {
            Mode = EndpointMode.BufferedInMemory
        };
        endpoint.Compile(theRuntime);

        return new BufferedReceiver(endpoint, theRuntime,
            new HandlerPipeline((WolverineRuntime)theRuntime, (WolverineRuntime)theRuntime, endpoint));
    }

    private static Envelope pingEnvelope(string name)
    {
        return new Envelope(new NativeAckPing(name)) { MessageType = typeof(NativeAckPing).ToMessageTypeName() };
    }

    [Fact]
    public async Task a_single_delivery_after_the_drain_is_deferred_not_completed()
    {
        var receiver = bufferedReceiver();
        var listener = new RecordingListener();

        await receiver.DrainAsync();

        await receiver.ReceivedAsync(listener, pingEnvelope("late"));
        await listener.WaitUntilAtLeast(1);

        listener.Deferred.Count.ShouldBe(1);
        listener.Completed.ShouldBeEmpty();
        NativeAckPingHandler.Handled.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_batch_delivered_after_the_drain_is_deferred_not_completed()
    {
        var receiver = bufferedReceiver();
        var listener = new RecordingListener();

        await receiver.DrainAsync();

        await ((IReceiver)receiver).ReceivedAsync(listener, [pingEnvelope("one"), pingEnvelope("two")]);
        await listener.WaitUntilAtLeast(2);

        listener.Deferred.Count.ShouldBe(2);
        listener.Completed.ShouldBeEmpty();
        NativeAckPingHandler.Handled.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_delivery_before_the_drain_still_runs_and_is_completed()
    {
        var receiver = bufferedReceiver();
        var listener = new RecordingListener();

        await receiver.ReceivedAsync(listener, pingEnvelope("on-time"));
        await listener.WaitUntilAtLeast(1);

        listener.Completed.Count.ShouldBe(1);
        listener.Deferred.ShouldBeEmpty();
    }
}
