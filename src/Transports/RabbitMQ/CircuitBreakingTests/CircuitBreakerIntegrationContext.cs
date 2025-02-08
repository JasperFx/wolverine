using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit.Abstractions;

namespace CircuitBreakingTests;

[Collection("circuit_breaker")]
public abstract class CircuitBreakerIntegrationContext : IDisposable, IObserver<IWolverineEvent>
{
    private readonly IHost _host;
    private readonly ITestOutputHelper _output;
    private readonly Random _random = new();

    private readonly List<ListenerState> _recordedStates = new();
    private readonly WolverineRuntime _runtime;
    private readonly List<Task> _tasks = new();

    public CircuitBreakerIntegrationContext(ITestOutputHelper output)
    {
        _output = output;
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(configureListener).UseResourceSetupOnStartup(StartupAction.ResetState)
            .ConfigureServices(services =>
            {
                services.AddSingleton(output);
                services.AddSingleton(typeof(ILogger<>), typeof(OutputLogger<>));
            })
            .Start();

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>().As<WolverineRuntime>();
        _runtime.Tracker.Subscribe(this);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    void IObserver<IWolverineEvent>.OnCompleted()
    {
    }

    void IObserver<IWolverineEvent>.OnError(Exception error)
    {
    }

    void IObserver<IWolverineEvent>.OnNext(IWolverineEvent value)
    {
        if (value is ListenerState state)
        {
            _output.WriteLine($"Got status update {state.Status} with {Recorder.Received} processed");
            _recordedStates.Add(state);
        }
    }

    protected abstract void configureListener(WolverineOptions opts);

    protected void assertTheCircuitBreakerNeverTripped()
    {
        _recordedStates.Any(x => x.Status == ListeningStatus.Stopped).ShouldBeFalse();
    }

    protected void assertTheCircuitBreakerTripped()
    {
        _recordedStates.Any(x => x.Status == ListeningStatus.Stopped).ShouldBeTrue();
    }

    protected void assertTheCircuitBreakerWasReset()
    {
        assertTheCircuitBreakerTripped();

        _recordedStates.Last().Status.ShouldBe(ListeningStatus.Accepting);
    }

    protected SometimesFails[] buildHundredMessages(int failurePercent)
    {
        var everyOther = Math.Floor((double)(100 / failurePercent));

        var messages = new SometimesFails[100];

        for (var i = 0; i < messages.Length; i++)
        {
            var shouldFail = i > 0 && i % everyOther == 0;

            var message = new SometimesFails(i, shouldFail ? MessageResult.BadImage : MessageResult.Success,
                MessageResult.Success, MessageResult.Success);

            messages[i] = message;
        }

        return messages;
    }

    protected void publishHundredMessagesNow(int failures)
    {
        var messages = buildHundredMessages(failures);
        var publisher = new MessageBus(_runtime);
        var task = Task.Factory.StartNew(async () =>
        {
            foreach (var message in messages) await publisher.PublishAsync(message);

            _output.WriteLine($"Finished publishing a batch with {failures}% failures");
        });

        _tasks.Add(task);
    }

    protected void delayPublishHundredMessages(TimeSpan delay, int failures)
    {
        var messages = buildHundredMessages(failures);
        var publisher = new MessageBus(_runtime);
        var task = Task.Factory.StartNew(async () =>
        {
            await Task.Delay(delay);
            _output.WriteLine($"Starting to publish a batch with {failures}% failures");
            foreach (var message in messages) await publisher.PublishAsync(message);

            _output.WriteLine($"Finished publishing a batch with {failures}% failures");
        });

        _tasks.Add(task);
    }

    protected Task afterAllMessagesArePublished()
    {
        return Task.WhenAll(_tasks);
    }

    [Fact]
    public async Task everything_is_wonderful_even_though_there_are_some_failures_so_do_not_ever_trip()
    {
        var messageWaiter = Recorder.WaitForMessagesToBeProcessed(_output, 1200, 2.Minutes());

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

        assertTheCircuitBreakerNeverTripped();
    }

    [Fact]
    public async Task the_circuit_breaker_should_trip_and_restart()
    {
        var messageWaiter = Recorder.WaitForMessagesToBeProcessed(_output, 1200, 1.Minutes());

        publishHundredMessagesNow(10);
        publishHundredMessagesNow(80);
        publishHundredMessagesNow(10);
        publishHundredMessagesNow(25);
        publishHundredMessagesNow(25);

#pragma warning disable CS4014
        Task.Factory.StartNew(async () =>
#pragma warning restore CS4014
        {
            await Task.Delay(10.Seconds());
            Recorder.NeverFail = true;
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

        assertTheCircuitBreakerTripped();
        assertTheCircuitBreakerWasReset();
    }
}

public enum MessageResult
{
    Success,
    DivideByZero,
    BadImage
}

public record SometimesFails(int Number, MessageResult First, MessageResult Second, MessageResult Third);

public class OutputLogger<T> : ILogger<T>, IDisposable
{
    private readonly ITestOutputHelper _output;

    public OutputLogger(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _output.WriteLine(formatter(state, exception));
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
        //return typeof(T).Name == "DurabilityAgent";
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        return this;
    }
}