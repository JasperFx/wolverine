using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace CircuitBreakingTests;

public class stopping_and_starting_listeners : IDisposable
{
    private readonly int _port1;
    private readonly int _port2;
    private readonly int _port3;
    private readonly IHost theListener;

    public stopping_and_starting_listeners()
    {
        _port1 = PortFinder.GetAvailablePort();
        _port2 = PortFinder.GetAvailablePort();
        _port3 = PortFinder.GetAvailablePort();

        theListener = WolverineHost.For(opts =>
        {
            opts.ListenAtPort(_port1).Named("one");
            opts.ListenAtPort(_port2).Named("two");
            opts.ListenAtPort(_port3).Named("three");

            opts.Policies.OnException<DivideByZeroException>()
                .Requeue().AndPauseProcessing(5.Seconds());
        });
    }

    public void Dispose()
    {
        theListener?.Dispose();
    }

    [Fact]
    public void find_listener_by_name()
    {
        var runtime = theListener.GetRuntime();
        runtime.Endpoints.FindListeningAgent("one")
            .Uri.ShouldBe($"tcp://localhost:{_port1}".ToUri());

        runtime.Endpoints.FindListeningAgent("wrong")
            .ShouldBeNull();
    }

    [Fact]
    public void all_listeners_are_initially_listening()
    {
        var uri1 = $"tcp://localhost:{_port1}".ToUri();
        var uri2 = $"tcp://localhost:{_port2}".ToUri();
        var uri3 = $"tcp://localhost:{_port3}".ToUri();

        var runtime = theListener.GetRuntime();

        runtime.Endpoints.FindListeningAgent(uri1).Status.ShouldBe(ListeningStatus.Accepting);
        runtime.Endpoints.FindListeningAgent(uri2).Status.ShouldBe(ListeningStatus.Accepting);
        runtime.Endpoints.FindListeningAgent(uri3).Status.ShouldBe(ListeningStatus.Accepting);
    }

    [Fact]
    public void unknown_listener_is_unknown()
    {
        theListener.GetRuntime().Endpoints.FindListeningAgent("unknown://server".ToUri())
            .ShouldBeNull();
    }

    [Fact]
    public async Task stop_with_no_restart()
    {
        var agent = theListener.GetRuntime().Endpoints.FindListeningAgent("one");
        await agent.StopAndDrainAsync();

        agent.Status.ShouldBe(ListeningStatus.Stopped);

        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
    }

    [Fact]
    public async Task pause()
    {
        var agent = theListener.GetRuntime().Endpoints.FindListeningAgent("one");
        await agent.PauseAsync(3.Seconds());

        agent.Status.ShouldBe(ListeningStatus.Stopped);

        var stopwatch = new Stopwatch();
        stopwatch.Start();

        while (stopwatch.Elapsed < 10.Seconds())
        {
            if (agent.Status == ListeningStatus.Accepting)
            {
                stopwatch.Stop();
                return;
            }
        }

        agent.Status.ShouldBe(ListeningStatus.Accepting);
    }

    [Fact]
    public async Task pause_repeatedly()
    {
        var agent = theListener.GetRuntime().Endpoints.FindListeningAgent("one");
        await agent.PauseAsync(1.Seconds());
        await agent.PauseAsync(1.Seconds());
        await agent.PauseAsync(3.Seconds());

        agent.Status.ShouldBe(ListeningStatus.Stopped);

        var stopwatch = new Stopwatch();
        stopwatch.Start();

        while (stopwatch.Elapsed < 10.Seconds())
        {
            if (agent.Status == ListeningStatus.Accepting)
            {
                stopwatch.Stop();
                return;
            }
        }

        agent.Status.ShouldBe(ListeningStatus.Accepting);
    }

    [Fact]
    public async Task pause_listener_on_matching_error_condition()
    {
        using var sender = WolverineHost.For(opts => { opts.PublishAllMessages().ToPort(_port1).Named("one"); });

        var runtime = theListener.GetRuntime();

        var stopWaiter =
            runtime.Tracker.WaitForListenerStatusAsync("one", ListeningStatus.Stopped, 1.Minutes());

        await sender
            .TrackActivity()
            .AlsoTrack(theListener)
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new PausingMessage());

        await stopWaiter;

        var agent = runtime.Endpoints.FindListeningAgent("one");
        agent.Status.ShouldBe(ListeningStatus.Stopped);

        // should restart
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        while (stopwatch.Elapsed < 10.Seconds())
        {
            if (agent.Status == ListeningStatus.Accepting)
            {
                stopwatch.Stop();
                return;
            }
        }

        agent.Status.ShouldBe(ListeningStatus.Accepting);
    }
}

public class PausingMessage
{
}

public class PausingMessageHandler
{
    public static void Handle(PausingMessage message, Envelope envelope)
    {
        if (envelope.Attempts <= 1)
        {
            throw new DivideByZeroException("boom");
        }
    }
}