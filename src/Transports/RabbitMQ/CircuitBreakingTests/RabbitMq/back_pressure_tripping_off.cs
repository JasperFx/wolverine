using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit.Abstractions;

namespace CircuitBreakingTests.RabbitMq;

public class back_pressure_tripping_off(ITestOutputHelper output) : IAsyncLifetime
{
    private IHost _host = null!;

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
                opts.Services.AddSingleton<Recorder>();
            })

            // This builds any missing Rabbit MQ objects if they don't exist,
            // but purges existing queues so we start from a clean slate
            .UseResourceSetupOnStartup(StartupAction.ResetState)
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.TeardownResources();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task back_pressure_trips_with_buffered_receiver()
    {
        var recorder = _host.Services.GetRequiredService<Recorder>();
        // Make the tests run slow so they'll back up very easily
        SometimesSlowMessageHandler.RunSlow = true;

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var statusRecorder = new StatusRecorder(output);
        runtime.Tracker.Subscribe(statusRecorder);

        var waitForTooBusy =
            runtime.Tracker.WaitForListenerStatusAsync("incoming", ListeningStatus.TooBusy, 1.Minutes());

        var completion = recorder.WaitForMessagesToBeProcessed(1000, 1.Minutes());

        var publishing = Task.Run(async () =>
        {
            var publisher = _host.MessageBus();

            for (var i = 0; i < 1000; i++)
            {
                await publisher.EndpointFor("incoming").SendAsync( new SometimesSlowMessage());
            }
        });

        await waitForTooBusy;

        // If it's tripped off, let's speed it up!
        SometimesSlowMessageHandler.RunSlow = false;

        await publishing;

        // Got all the messages in the end
        await completion;

        statusRecorder.StateChanges.ShouldContain(x => x.Status == ListeningStatus.TooBusy);
    }

    public class StatusRecorder(ITestOutputHelper output) : IObserver<IWolverineEvent>
    {
        private readonly ITestOutputHelper _output = output;
        public readonly List<ListenerState> StateChanges = [];

        public void OnCompleted()
        {
            // nothing
        }

        public void OnError(Exception error)
        {
            // nothing
        }

        public void OnNext(IWolverineEvent value)
        {
            if (value is ListenerState state)
            {
                _output.WriteLine($"Changed to {state.Status}");
                StateChanges.Add(state);
            }
        }
    }
}

public class SometimesSlowMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class SometimesSlowMessageHandler
{
    public static bool RunSlow;

    public static async Task Handle(SometimesSlowMessage message, Recorder recorder)
    {
        if (RunSlow)
        {
            await Task.Delay(2.Seconds());
        }

        recorder.Increment(message.Id);
    }
}