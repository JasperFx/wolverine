using JasperFx.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4797. <see cref="DurableReceiver.DrainAsync"/> waits for in-flight handlers with
/// <c>Task.WhenAny(completion, Task.Delay(DrainTimeout))</c>, and then runs a blanket
/// <c>ReleaseIncomingAsync</c> for the address unconditionally. Past the timeout that hands every row back
/// to <c>AnyNode</c> while handlers are still running, so another node can claim and re-execute a message
/// this one has not finished -- and for an exclusive listener, or a global partition slot handing over
/// (GH-4777), that is exactly the intra-group concurrency the partitioned modes forbid.
///
/// <para>
/// The fix is to HOLD the release until that work actually finishes, rather than release underneath it. The
/// drain itself still returns promptly -- blocking it would turn a correctness problem into a slot served
/// nowhere, since <c>ReassignAgent</c> gates the gaining node's start on the stop returning -- so the wait
/// happens on a continuation. These tests pin both halves: nothing is released while a handler is running,
/// and the release does happen once it finishes.
/// </para>
/// </summary>
public class drain_timeout_is_logged_4797
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly RecordingLoggerProvider theLogs = new();

    private readonly TaskCompletionSource _handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DurableReceiver theReceiver = null!;

    public drain_timeout_is_logged_4797()
    {
        theRuntime.LoggerFactory.AddProvider(theLogs);

        // Short enough to keep the test honest about waiting, long enough not to race the enqueue.
        theRuntime.DurabilitySettings.DrainTimeout = 250.Milliseconds();
    }

    private DurableReceiver buildReceiver()
    {
        // A local-queue-shaped endpoint, so the receiver takes the durable path
        return new DurableReceiver(new StubEndpoint("4797", new StubTransport()), theRuntime, thePipeline);
    }

    /// <summary>
    /// The handler is still running when the drain gives up, which is the only state the warning is about.
    /// </summary>
    [Fact]
    public async Task warns_when_the_drain_gives_up_on_an_in_flight_handler()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>())
            .Returns(_ =>
            {
                _handlerEntered.TrySetResult();
                return _releaseHandler.Task;
            });

        theReceiver = buildReceiver();

        var envelope = ObjectMother.Envelope();
        envelope.WasPersistedInInbox = true;
        await theReceiver.EnqueueAsync(envelope);

        // Latching before the handler is actually inside InvokeAsync would make execute() return early and
        // there would be nothing in flight to time out on -- the test would pass for the wrong reason.
        await _handlerEntered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        theReceiver.Latch();
        await theReceiver.DrainAsync();

        var warning = theLogs.Records.SingleOrDefault(x => x.Level == LogLevel.Warning);

        warning.ShouldNotBeNull("The drain timed out with a handler still running and said nothing about it");
        warning.Message.ShouldContain("timed out");
        warning.Message.ShouldContain("GH-4797");
        warning.Message.ShouldContain("stub://4797");

        // The point of the fix: the rows are NOT handed to AnyNode underneath the running handler.
        await theRuntime.Storage.Inbox.DidNotReceive()
            .ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>());

        _releaseHandler.TrySetResult();
    }

    /// <summary>
    /// The other half. Holding the rows is only correct if the hold is eventually let go -- otherwise this
    /// trades double execution for rows nothing can ever claim, which is the GH-3856 direction and worse.
    /// </summary>
    [Fact]
    public async Task releases_the_held_rows_once_the_in_flight_handler_finishes()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>())
            .Returns(_ =>
            {
                _handlerEntered.TrySetResult();
                return _releaseHandler.Task;
            });

        theReceiver = buildReceiver();

        var envelope = ObjectMother.Envelope();
        envelope.WasPersistedInInbox = true;
        await theReceiver.EnqueueAsync(envelope);
        await _handlerEntered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        theReceiver.Latch();
        await theReceiver.DrainAsync();

        // Let the handler finish. The deferred release runs on a continuation, so poll rather than assume
        // it has happened by the time this line returns.
        _releaseHandler.TrySetResult();

        await waitForAsync(
            () => theRuntime.Storage.Inbox.ReceivedCalls()
                .Any(x => x.GetMethodInfo().Name == nameof(IMessageInbox.ReleaseIncomingAsync)),
            "The held inbox rows were never released after the in-flight handler finished");
    }

    private static async Task waitForAsync(Func<bool> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50.Milliseconds());
        }

        throw new TimeoutException(message);
    }

    /// <summary>
    /// The negative control, and the one that keeps this from becoming noise on every ordinary shutdown. A
    /// drain with nothing in flight completes rather than times out, and must stay quiet.
    /// </summary>
    [Fact]
    public async Task stays_quiet_when_the_drain_completes()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>()).Returns(Task.CompletedTask);

        theReceiver = buildReceiver();

        theReceiver.Latch();
        await theReceiver.DrainAsync();

        theLogs.Records.ShouldNotContain(x => x.Level == LogLevel.Warning);

        // And a clean drain still releases inline, exactly as before -- the fix must not make the ordinary
        // shutdown path defer anything.
        await theRuntime.Storage.Inbox.Received()
            .ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>());
    }
}

/// <summary>
/// Minimal capturing provider. MockWolverineRuntime hands out a real <see cref="LoggerFactory"/> with no
/// providers, and <c>AddProvider</c> reaches loggers it has already created, so this works whether it is
/// registered before or after the receiver is built.
/// </summary>
public class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<LogRecord> _records = new();

    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_records) return _records.ToArray();
        }
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

    public void Dispose()
    {
    }

    private void record(LogLevel level, string message)
    {
        lock (_records) _records.Add(new LogRecord(level, message));
    }

    public record LogRecord(LogLevel Level, string Message);

    private class RecordingLogger : ILogger
    {
        private readonly RecordingLoggerProvider _parent;

        public RecordingLogger(RecordingLoggerProvider parent)
        {
            _parent = parent;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _parent.record(logLevel, formatter(state, exception));
        }
    }
}
