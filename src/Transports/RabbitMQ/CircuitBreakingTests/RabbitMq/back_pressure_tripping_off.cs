using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit.Abstractions;

namespace CircuitBreakingTests.RabbitMq;

public class back_pressure_tripping_off(ITestOutputHelper output) : IAsyncLifetime
{
    private IHost _host = null!;
    private ListenerObserver _listenerObserver = null!;
    private IWolverineRuntime _runtime = null!;
    private IDisposable _trackSubscription = null!;

    public async Task InitializeAsync()
    {
        var queueName = $"{GetType().Name}_{DateTime.UtcNow:yyyyMMddHHmmss}";

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenToRabbitQueue(queueName).Named("incoming")
                    // Setting it so that the endpoint should trip off with back
                    // pressure pretty easily
                    .BufferedInMemory(new BufferingLimits(100, 50));

                opts.Services.AddLogging(x => x.AddXunitLogging(output));
                opts.Services.AddSingleton<ListenerObserver>();
                opts.Services.AddSingleton<MessageRecorder>();
            })

            // This builds any missing Rabbit MQ objects if they don't exist,
            // but purges existing queues so we start from a clean slate
            .UseResourceSetupOnStartup(StartupAction.ResetState)
            .StartAsync();

        _listenerObserver = _host.Services.GetRequiredService<ListenerObserver>();
        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        _trackSubscription = _runtime.Tracker.Subscribe(_listenerObserver);
    }

    public async Task DisposeAsync()
    {
        _trackSubscription.Dispose();
        await _host.TeardownResources();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task back_pressure_trips_with_buffered_receiver()
    {
        var recorder = _host.Services.GetRequiredService<MessageRecorder>();
        // Make the tests run slow so they'll back up very easily
        SometimesSlowMessageHandler.RunSlow = true;

        var waitForTooBusy = _runtime.Tracker.WaitForListenerStatusAsync(
            "incoming", ListeningStatus.TooBusy, 1.Minutes());

        var completion = recorder.WaitForMessagesToBeProcessed(1000, 1.Minutes());

        var publishing = Task.Run(async () =>
        {
            var publisher = _host.MessageBus();

            for (var i = 0; i < 1000; i++)
            {
                var message = new SometimesSlowMessage();
                await publisher.EndpointFor("incoming")
                    .SendAsync(message);
                recorder.TrackPublished(message.Id);
            }
        });

        await waitForTooBusy;

        // If it's tripped off, let's speed it up!
        SometimesSlowMessageHandler.RunSlow = false;

        await publishing;

        // Got all the messages in the end
        await completion;

        _listenerObserver.RecordedStates.ShouldContain(ListeningStatus.TooBusy);
    }
}

public class SometimesSlowMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class SometimesSlowMessageHandler
{
    public static bool RunSlow;

    public static async Task Handle(SometimesSlowMessage message, MessageRecorder recorder)
    {
        if (RunSlow)
        {
            await Task.Delay(2.Seconds());
        }

        recorder.Increment(message.Id);
    }
}