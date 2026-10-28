using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.ErrorHandling;

// GH-5257. RetryInlineContinuation waited out its whole cooldown with no way to be interrupted, then retried
// unconditionally. A listener that joins the processing of the messages it handed out -- the Azure Service Bus
// session listener since #5220 -- could not finish stopping until the cooldown ended, and the handler then ran
// again after the stop. Host shutdown could not help either: it cancels runtime.Cancellation only after every
// endpoint has drained. A real stop now interrupts the cooldown first, and the interrupted message is left
// unsettled for redelivery or recovery.
public class retry_cooldown_stops_with_its_listener_5257
{
    [Fact]
    public async Task a_cooldown_ends_early_without_retrying_when_its_listener_stops()
    {
        using var host = await startHostAsync();
        var (lifecycle, pipeline) = lifecycleOnAPipeline(host);

        var retrying = new RetryInlineContinuation(1.Minutes())
            .ExecuteAsync(lifecycle, host.GetRuntime(), DateTimeOffset.UtcNow, null).AsTask();

        await pipeline.SignalStoppingAsync();

        await retrying.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
        await lifecycle.DidNotReceive().RetryExecutionNowAsync();
    }

    [Fact]
    public async Task a_cooldown_that_is_not_interrupted_still_retries()
    {
        using var host = await startHostAsync();
        var (lifecycle, _) = lifecycleOnAPipeline(host);

        await new RetryInlineContinuation(50.Milliseconds())
            .ExecuteAsync(lifecycle, host.GetRuntime(), DateTimeOffset.UtcNow, null);

        await lifecycle.Received(1).RetryExecutionNowAsync();
    }

    [Fact]
    public async Task a_restarted_listener_retries_again()
    {
        using var host = await startHostAsync();
        var (lifecycle, pipeline) = lifecycleOnAPipeline(host);

        await pipeline.SignalStoppingAsync();
        pipeline.ResetStopping();

        await new RetryInlineContinuation(50.Milliseconds())
            .ExecuteAsync(lifecycle, host.GetRuntime(), DateTimeOffset.UtcNow, null);

        await lifecycle.Received(1).RetryExecutionNowAsync();
    }

    [Fact]
    public async Task stopping_a_listener_is_not_held_by_a_retry_cooldown()
    {
        var port = PortFinder.GetAvailablePort();
        using var host = await startHostAsync(opts =>
        {
            opts.ListenAtPort(port);
            opts.PublishMessage<CooldownMessage>().ToPort(port);
        });

        var id = Guid.NewGuid();
        await host.MessageBus().PublishAsync(new CooldownMessage(id));
        await CooldownMessageHandler.FirstAttemptOf(id).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var agent = host.GetRuntime().Endpoints.FindListeningAgent(new Uri($"tcp://localhost:{port}"))!;

        var watch = Stopwatch.StartNew();
        await agent.StopAndDrainAsync().AsTask().WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        watch.Elapsed.ShouldBeLessThan(10.Seconds());

        // No retry after the stop
        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        CooldownMessageHandler.AttemptsOf(id).ShouldBe(1);
    }

    [Fact]
    public async Task host_shutdown_is_not_held_by_a_retry_cooldown()
    {
        var port = PortFinder.GetAvailablePort();
        var host = await startHostAsync(opts =>
        {
            opts.ListenAtPort(port);
            opts.PublishMessage<CooldownMessage>().ToPort(port);
        });

        var id = Guid.NewGuid();
        await host.MessageBus().PublishAsync(new CooldownMessage(id));
        await CooldownMessageHandler.FirstAttemptOf(id).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The cooldown is a minute and DrainTimeout is 30 seconds, so before GH-5257 this took at least 30
        await host.StopAsync(TestContext.Current.CancellationToken).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        host.Dispose();

        CooldownMessageHandler.AttemptsOf(id).ShouldBe(1);
    }

    [Fact]
    public async Task short_cooldowns_still_retry_and_then_dead_letter_with_the_failure()
    {
        using var host = await startHostAsync(opts =>
        {
            opts.PublishMessage<QuickCooldownMessage>().ToLocalQueue("quick-cooldown");
        });

        var id = Guid.NewGuid();
        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new QuickCooldownMessage(id));

        CooldownMessageHandler.AttemptsOf(id).ShouldBe(3);

        var deadLettered = session.MovedToErrorQueue.SingleEnvelope<QuickCooldownMessage>();
        deadLettered.Message.ShouldBeOfType<QuickCooldownMessage>().Id.ShouldBe(id);
        session.AllExceptions().ShouldContain(x => x is CooldownException);
    }

    private static Task<IHost> startHostAsync(Action<WolverineOptions>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<CooldownMessageHandler>();
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.OnException<CooldownException>()
                    .RetryWithCooldown(1.Minutes())
                    .Then.MoveToErrorQueue();

                configure?.Invoke(opts);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private static (IEnvelopeLifecycle, HandlerPipeline) lifecycleOnAPipeline(IHost host)
    {
        var runtime = host.GetRuntime();
        var pipeline = new HandlerPipeline(runtime, runtime);

        var listener = Substitute.For<IListener>();
        listener.Pipeline.Returns(pipeline);

        var envelope = new Envelope { Attempts = 2 };
        envelope.MarkReceived(listener, DateTimeOffset.UtcNow, runtime.DurabilitySettings);

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);

        return (lifecycle, pipeline);
    }
}

public record CooldownMessage(Guid Id);

public record QuickCooldownMessage(Guid Id);

public class CooldownException(string message) : Exception(message);

public class CooldownMessageHandler
{
    private static readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _firstAttempts = new();

    public static int AttemptsOf(Guid id) => _attempts.GetValueOrDefault(id);

    public static Task FirstAttemptOf(Guid id) => firstAttempt(id).Task;

    private static TaskCompletionSource firstAttempt(Guid id) =>
        _firstAttempts.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    // Fails every time, so the one-minute cooldown policy always applies
    public static void Handle(CooldownMessage message)
    {
        _attempts.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
        firstAttempt(message.Id).TrySetResult();

        throw new CooldownException("Always fails");
    }

    public static void Handle(QuickCooldownMessage message)
    {
        _attempts.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
        throw new CooldownException("Always fails");
    }

    // The quick message gets a cooldown short enough to run out, ahead of the global one-minute policy
    public static void Configure(Wolverine.Runtime.Handlers.HandlerChain chain)
    {
        if (chain.MessageType == typeof(QuickCooldownMessage))
        {
            chain.OnException<CooldownException>()
                .RetryWithCooldown(50.Milliseconds(), 50.Milliseconds())
                .Then.MoveToErrorQueue();
        }
    }
}
