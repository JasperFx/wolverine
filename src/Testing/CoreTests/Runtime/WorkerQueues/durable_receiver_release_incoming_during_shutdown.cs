using System.Diagnostics;
using System.Net.Sockets;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// Regression coverage for GH-2671: <see cref="DurableReceiver.DrainAsync"/> must
/// terminate within a reasonable time even when the underlying message store is
/// throwing on every attempt to release inbox ownership. The pre-fix
/// <c>executeWithRetriesAsync</c> was an unbounded <c>while (true)</c> that
/// (a) ignored the cancellation token, (b) logged every failure at Error level,
/// and (c) never gave up — so during a shutdown sequence, where the Npgsql
/// <c>DbDataSource</c> has already been disposed, the loop would spin forever
/// against a dead socket and emit a flood of <c>SocketException</c> log noise.
/// </summary>
public class durable_receiver_release_incoming_during_shutdown : IAsyncLifetime
{
    private readonly IHandlerPipeline _pipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime _runtime;
    private readonly DurableReceiver _receiver;

    public durable_receiver_release_incoming_during_shutdown()
    {
        _runtime = new MockWolverineRuntime();

        // Simulate the user's reported failure mode: every call to ReleaseIncomingAsync
        // throws SocketException because the Npgsql connector can't reach the server
        // anymore. The exact exception type matches the Npgsql shutdown path the user
        // reported in the issue; the implementation only needs *some* exception to
        // demonstrate the retry-loop bug.
        _runtime.Storage.Inbox
            .When(x => x.ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>()))
            .Do(_ => throw new SocketException(10054 /* WSAECONNRESET */));

        var endpoint = new StubEndpoint("test://2671", new StubTransport());
        _receiver = new DurableReceiver(endpoint, _runtime, _pipeline);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task drain_terminates_within_seconds_when_inbox_release_throws_repeatedly()
    {
        // Shutdown not signalled — the bounded retry path applies. With
        // MaxReleaseRetries = 5 and linear backoff (100ms, 200ms, 300ms, 400ms),
        // the upper bound is around 1s of sleep + per-attempt work. Anything
        // under five seconds proves we are no longer in an unbounded loop.
        var sw = Stopwatch.StartNew();
        await _receiver.DrainAsync();
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5),
            "DrainAsync looped beyond the bounded retry budget — the GH-2671 unbounded " +
            "while-true regression has returned.");
    }

    [Fact]
    public async Task drain_exits_immediately_when_cancellation_is_signalled()
    {
        // Cancel the durability cancellation token before draining — this is the
        // shape of the user-reported scenario where the host is shutting down and
        // the connection pool has already gone away. We expect DrainAsync to bail
        // out on the very first failure rather than burn the full retry budget.
        _runtime.DurabilitySettings.Cancel();

        var sw = Stopwatch.StartNew();
        await _receiver.DrainAsync();
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1),
            "Shutdown-aware exit didn't fire — cancellation signal must short-circuit " +
            "the retry loop on the first failure.");
    }
}
