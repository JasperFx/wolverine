using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit.Abstractions;

namespace CircuitBreakingTests.RabbitMq;

public class back_pressure_tripping_off
{
    private readonly ITestOutputHelper _output;

    public back_pressure_tripping_off(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task back_pressure_trips_with_buffered_receiver()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenToRabbitQueue("pressure").Named("incoming")

                    // Setting it so that the endpoint should trip off with back
                    // pressure pretty easily
                    .BufferedInMemory(new BufferingLimits(100, 50));
            })

            // This builds any missing Rabbit MQ objects if they don't exist,
            // but purges existing queues so we start from a clean slate
            .UseResourceSetupOnStartup(StartupAction.ResetState)
            .StartAsync();

        // Make the tests run slow so they'll back up very easily
        SometimesSlowMessageHandler.RunSlow = true;

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var statusRecorder = new StatusRecorder(_output);
        runtime.Tracker.Subscribe(statusRecorder);

        var waitForTooBusy =
            runtime.Tracker.WaitForListenerStatusAsync("incoming", ListeningStatus.TooBusy, 1.Minutes());

        var completion = Recorder.WaitForMessagesToBeProcessed(_output, 1000, 1.Minutes());

        var publishing = Task.Factory.StartNew(async () =>
        {
            var publisher = host.MessageBus();

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

    public class StatusRecorder : IObserver<IWolverineEvent>
    {
        private readonly ITestOutputHelper _output;
        public readonly List<ListenerState> StateChanges = new();

        public StatusRecorder(ITestOutputHelper output)
        {
            _output = output;
        }

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
                _output.WriteLine("Changed to " + state.Status);
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

    public static async Task Handle(SometimesSlowMessage message)
    {
        if (RunSlow)
        {
            await Task.Delay(2.Seconds());
        }

        Recorder.Increment();
    }
}