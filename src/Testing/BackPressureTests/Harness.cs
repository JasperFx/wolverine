using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;


namespace BackPressureTests;

public class MassSender(IHost sender)
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task _task;

    public void Cancel()
    {
        _cancellation.Cancel();
    }
    
    public void StartPublishing(int maximum = 5000, TimeSpan? time = null)
    {
        if (time != null)
        {
            _cancellation.CancelAfter(time.Value);
        }

        var runtime = sender.GetRuntime();
        
        _task = Task.Run(async () =>
        {
            for (int i = 0; i < maximum; i++)
            {
                if (_cancellation.IsCancellationRequested) return;
                var bus = new MessageBus(runtime);
                await bus.PublishAsync(new Message1(Guid.NewGuid()));
                await bus.PublishAsync(new Message2(Guid.NewGuid()));
                await bus.PublishAsync(new Message3(Guid.NewGuid()));
                await bus.PublishAsync(new Message4(Guid.NewGuid()));
            }
        });
    }
}

public class Harness : IAsyncLifetime, IWolverineActivator
{
    private IHost _sender;
    private XUnitObserver theObserver;
    private IHost _receiver;
    public static bool GoSlow { get; set; } = true;

    public Harness(ITestOutputHelper output)
    {
        theObserver = new XUnitObserver(output);
    }

    void IWolverineActivator.Apply(IWolverineRuntime runtime)
    {
        runtime.Observer = theObserver;
    }

    public async Task InitializeAsync()
    {
        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToRabbitQueue("bp");
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // for callbacks
                opts.Services.AddSingleton<IWolverineActivator>(this);
                
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bp";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.ListenToRabbitQueue("bp").UseDurableInbox();
                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        _receiver.GetRuntime().Observer = theObserver;
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }

    [Fact]
    public async Task lets_see_if_we_can_trip_off_back_pressure_and_see_it_lifted()
    {
        Harness.GoSlow = true;
        var sender = new MassSender(_sender);
        sender.StartPublishing(20000);

        await theObserver.Triggered.Task.TimeoutAfterAsync(90000);

        Harness.GoSlow = false;
        
        sender.Cancel();

        await theObserver.Lifted.Task.TimeoutAfterAsync(90000);
    }
}

public record Message1(Guid Id);
public record Message2(Guid Id);
public record Message3(Guid Id);
public record Message4(Guid Id);

public static class MessageHandler
{
    public static async Task HandleAsync(Message1 m)
    {
        if (Harness.GoSlow)
        {
            await Task.Delay(Random.Shared.Next(100, 500));
        }
    }
    
    public static async Task HandleAsync(Message2 m)
    {
        if (Harness.GoSlow)
        {
            await Task.Delay(Random.Shared.Next(100, 500));
        }
    }
    
    public static async Task HandleAsync(Message3 m)
    {
        if (Harness.GoSlow)
        {
            await Task.Delay(Random.Shared.Next(100, 500));
        }
    }
    
    public static async Task HandleAsync(Message4 m)
    {
        if (Harness.GoSlow)
        {
            await Task.Delay(Random.Shared.Next(100, 500));
        }
    }
}