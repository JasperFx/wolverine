using Microsoft.Extensions.Logging;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Transports;

public class BackPressureAgentTests
{
    private readonly BackPressureAgent theBackPressureAgent;
    private readonly Endpoint theEndpoint = new TcpEndpoint(5555);
    private readonly IListeningAgent theListeningAgent = Substitute.For<IListeningAgent>();
    private readonly IWolverineObserver theObserver;
    private readonly RecordingLogger theLogger = new();

    public BackPressureAgentTests()
    {
        theObserver = Substitute.For<IWolverineObserver>();
        theBackPressureAgent = new BackPressureAgent(theListeningAgent, theEndpoint, theObserver, theLogger);
    }

    private class RecordingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task do_nothing_when_accepting_and_under_the_threshold()
    {
        theListeningAgent.Status
            .Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount
            .Returns(theEndpoint.BufferingLimits.Maximum - 1);

        // Evaluate whether or not the listening should be paused
        // based on the current queued item count, the current status
        // of the listening agent, and the configured buffering limits
        // for the endpoint
        await theBackPressureAgent.CheckNowAsync();

        // Should decide NOT to do anything in this particular case
        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.DidNotReceive().StartAsync();
    }

    [Fact]
    public async Task do_nothing_when_accepting_at_the_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Maximum);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.DidNotReceive().StartAsync();
    }

    [Fact]
    public async Task stop_receiving_accepting_over_the_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Maximum + 1);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.Received().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.DidNotReceive().StartAsync();

        await theObserver.Received().BackPressureTriggered(theEndpoint, theListeningAgent);
    }

    [Fact]
    public async Task do_nothing_when_too_busy_and_over_the_restart_limit()
    {
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart + 1);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.DidNotReceive().StartAsync();
        
        await theObserver.DidNotReceive().BackPressureTriggered(theEndpoint, theListeningAgent);
    }

    [Fact]
    public async Task restart_when_too_busy_but_reached_the_restart_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.Received().StartAsync();
    }

    [Fact]
    public async Task never_restart_a_paused_listener_even_when_fully_drained()
    {
        // GH-3832 — Paused is operator intent (or a circuit-breaker trip with its own Restarter).
        // Back pressure relief must NOT resume it, no matter how empty the queue is.
        theListeningAgent.Status.Returns(ListeningStatus.Paused);
        theListeningAgent.QueueCount.Returns(0);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.DidNotReceive().StartAsync();
    }

    [Fact]
    public async Task restart_when_too_busy_but_below_the_restart_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart - 1);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.Received().StartAsync();
    }

    [Fact]
    public async Task warns_periodically_while_latched_and_not_draining()
    {
        // GH CritterWatch#922 — a latched listener used to log exactly one line when it stopped and
        // then nothing forever. Operators need a periodic sign of life carrying the numbers the
        // resume decision is made from.
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart + 100);

        for (var i = 0; i < BackPressureAgent.LatchedChecksPerReminder; i++)
        {
            await theBackPressureAgent.CheckNowAsync();
        }

        var warning = theLogger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("still latched by back pressure");

        // and it repeats on the next full interval rather than spamming every check
        for (var i = 0; i < BackPressureAgent.LatchedChecksPerReminder; i++)
        {
            await theBackPressureAgent.CheckNowAsync();
        }

        theLogger.Entries.Count.ShouldBe(2);

        // recovering resets the cadence
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart);
        await theBackPressureAgent.CheckNowAsync();
        await theListeningAgent.Received().StartAsync();
        theLogger.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task forces_a_full_rebuild_when_the_receiver_has_terminally_faulted()
    {
        // CritterWatch#942 — a faulted receiver's QueueCount is frozen, so neither the latch nor the
        // resume branch can ever act on it. The periodic check must notice the fault itself and force
        // the teardown/rebuild, regardless of the listener's reported status.
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart + 100);
        theListeningAgent.ReceiverHasFaulted.Returns(true);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.Received(1).RestartAsync(true);

        var critical = theLogger.Entries.ShouldHaveSingleItem();
        critical.Level.ShouldBe(LogLevel.Critical);
        critical.Message.ShouldContain("terminally faulted");
    }

    [Fact]
    public async Task faulted_receiver_recovery_fires_even_while_accepting()
    {
        // The Accepting-status zombie: receive loop still polling, every post failing. Nothing about
        // QueueCount thresholds applies — the fault check must run before the latch decision.
        theListeningAgent.Status.Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount.Returns(0);
        theListeningAgent.ReceiverHasFaulted.Returns(true);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.Received(1).RestartAsync(true);
        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
    }

    [Fact]
    public async Task no_rebuild_when_the_receiver_is_healthy()
    {
        theListeningAgent.Status.Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount.Returns(0);
        theListeningAgent.ReceiverHasFaulted.Returns(false);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().RestartAsync(Arg.Any<bool>());
    }
}