using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit.Abstractions;

namespace CircuitBreakingTests;

public abstract class CircuitBreakerIntegrationContext(ITestOutputHelper output)
    : IAsyncLifetime
{
    private readonly List<Task> _tasks = [];
    private IHost _host = null!;
    private MessageRecorder _recorder = null!;
    private WolverineRuntime _runtime = null!;
    private ListenerObserver _observer = null!;
    private IDisposable _trackerSubscription = null!;
    protected string _queueName = null!;

    public async Task InitializeAsync()
    {
        _queueName = $"{GetType().Name}_{DateTime.UtcNow:yyyyMMddHHmmss}";

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(configureListener)
            .UseResourceSetupOnStartup(StartupAction.ResetState)
            .ConfigureServices(services =>
            {
                services.AddLogging(x => x.AddXunitLogging(output));
                services.AddSingleton<MessageRecorder>();
                services.AddSingleton<ListenerObserver>();
            })
            .StartAsync();

        _recorder = _host.Services.GetRequiredService<MessageRecorder>();
        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>().As<WolverineRuntime>();
        _observer = _host.Services.GetRequiredService<ListenerObserver>();
        _trackerSubscription = _runtime.Tracker.Subscribe(_observer);
    }

    public async Task DisposeAsync()
    {
        _trackerSubscription.Dispose();
        await _host.TeardownResources();
        await _host.StopAsync();
        _host.Dispose();
    }

    protected abstract void configureListener(WolverineOptions opts);

    protected SometimesFails[] buildHundredMessages(int failurePercent)
    {
        var everyOther = Math.Floor((double)(100 / failurePercent));

        var messages = new SometimesFails[100];

        for (var i = 0; i < messages.Length; i++)
        {
            var shouldFail = i > 0 && i % everyOther == 0;

            var message = new SometimesFails(Guid.NewGuid(),
                shouldFail ? MessageResult.BadImage : MessageResult.Success,
                MessageResult.Success, MessageResult.Success);

            messages[i] = message;
        }

        return messages;
    }

    protected void publishHundredMessagesNow(int failures)
    {
        var messages = buildHundredMessages(failures);
        var publisher = _host.MessageBus();
        var task = Task.Run(async () =>
        {
            foreach (var message in messages)
            {
                await publisher.PublishAsync(message);
                _recorder.TrackPublished(message.Id);
            }

            output.WriteLine($"Finished publishing a batch with {failures}% failures");
        });

        _tasks.Add(task);
    }

    protected void delayPublishHundredMessages(TimeSpan delay, int failures)
    {
        var messages = buildHundredMessages(failures);
        var publisher = _host.MessageBus();
        var task = Task.Run(async () =>
        {
            await Task.Delay(delay);
            output.WriteLine($"Starting to publish a batch with {failures}% failures");

            foreach (var message in messages)
            {
                await publisher.PublishAsync(message);
                _recorder.TrackPublished(message.Id);
            }

            output.WriteLine($"Finished publishing a batch with {failures}% failures");
        });

        _tasks.Add(task);
    }

    protected Task afterAllMessagesArePublished()
    {
        return Task.WhenAll(_tasks);
    }

    // GH-3137: how long we allow the listener to churn through the published messages (with requeues,
    // and, in the trip test, a 10s circuit pause eating into the window). These assertions are about
    // circuit-breaker *behavior*, not throughput — on a slow/contended CI runner the old 1-minute
    // budget was itself the thing that failed. Kept generous so processing time is never under test.
    protected static readonly TimeSpan ProcessingBudget = 3.Minutes();

    // GH-3137: how many of the 1200 published messages must be processed for the trip test to pass.
    // Buffered variants override this below 1200 — buffered mode acks each message to the broker the
    // moment it lands in the in-memory buffer, so the handful caught in the listener teardown when the
    // breaker trips are lost (already acked, never persisted). That is buffered mode's documented
    // non-durable tradeoff; durable persists and inline has no buffer, so both still require all 1200.
    // The trip/restart *behavior* is still asserted for every variant.
    protected virtual int RequiredProcessedCountOnTrip => 1200;

    // GH-3137: the circuit's Accepting transition after a trip is emitted by the Restarter, PauseTime
    // (10s) after the trip and fully decoupled from message completion — the backlog can drain (and the
    // waiter resolve) while the listener is still paused. Synchronize to the listener's real lifecycle
    // instead of snapshotting RecordedStates at the instant messages happen to finish. Returns
    // immediately if the listener is already Accepting.
    protected Task waitForListenerToResumeAsync(TimeSpan timeout)
    {
        return _runtime.Tracker.WaitForListenerStatusAsync(_queueName, ListeningStatus.Accepting, timeout);
    }

    [Fact]
    public async Task everything_is_wonderful_even_though_there_are_some_failures_so_do_not_ever_trip()
    {
        var messageWaiter = _recorder.WaitForMessagesToBeProcessed(1200, ProcessingBudget);

        publishHundredMessagesNow(5);
        publishHundredMessagesNow(5);
        publishHundredMessagesNow(5);
        publishHundredMessagesNow(5);
        delayPublishHundredMessages(5.Seconds(), 5);
        delayPublishHundredMessages(5.Seconds(), 5);
        delayPublishHundredMessages(5.Seconds(), 5);
        delayPublishHundredMessages(5.Seconds(), 5);
        delayPublishHundredMessages(10.Seconds(), 5);
        delayPublishHundredMessages(10.Seconds(), 5);
        delayPublishHundredMessages(10.Seconds(), 5);
        delayPublishHundredMessages(10.Seconds(), 5);

        await afterAllMessagesArePublished();

        await messageWaiter;

        _observer.RecordedStates.ShouldNotContain(ListeningStatus.Stopped);
    }

    [Fact]
    public async Task the_circuit_breaker_should_trip_and_restart()
    {
        var messageWaiter = _recorder.WaitForMessagesToBeProcessed(RequiredProcessedCountOnTrip, ProcessingBudget);

        publishHundredMessagesNow(10);
        publishHundredMessagesNow(80);
        publishHundredMessagesNow(10);
        publishHundredMessagesNow(25);
        publishHundredMessagesNow(25);

        _ = Task.Run(async () =>
        {
            await Task.Delay(10.Seconds());
            _recorder.NeverFail = true;
        });

        delayPublishHundredMessages(5.Seconds(), 5);
        delayPublishHundredMessages(10.Seconds(), 5);
        delayPublishHundredMessages(10.Seconds(), 10);
        delayPublishHundredMessages(10.Seconds(), 5);
        delayPublishHundredMessages(15.Seconds(), 10);
        delayPublishHundredMessages(15.Seconds(), 10);
        delayPublishHundredMessages(15.Seconds(), 10);

        await afterAllMessagesArePublished();

        await messageWaiter;

        // It tripped at least once...
        _observer.RecordedStates.ShouldContain(ListeningStatus.Stopped);

        // ...and it recovers. Wait for the actual restart rather than asserting RecordedStates.Last()
        // at the instant messages finish — that snapshot races the Restarter's post-pause Accepting
        // (see waitForListenerToResumeAsync / GH-3137).
        await waitForListenerToResumeAsync(1.Minutes());
    }
}

public enum MessageResult
{
    Success,
    DivideByZero,
    BadImage
}

public record SometimesFails(Guid Id, MessageResult First, MessageResult Second, MessageResult Third);
