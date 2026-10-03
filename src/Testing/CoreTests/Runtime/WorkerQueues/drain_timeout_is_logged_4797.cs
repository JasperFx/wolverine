using JasperFx.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
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
/// <c>Task.WhenAny</c> discards which task won, so this used to happen with nothing logged at any level and
/// a stop that reported success. The underlying trade -- release only what was abandoned, or release nothing
/// and strand it -- is left to GH-4797; what is pinned here is that the degradation is no longer silent,
/// because an operator cannot act on something that leaves no trace.
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

        _releaseHandler.TrySetResult();
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
