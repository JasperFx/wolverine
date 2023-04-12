using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace ChaosTesting;

public interface IMessageStorageStrategy
{
    void ConfigureReceiverPersistence(WolverineOptions options);
    void ConfigureSenderPersistence(WolverineOptions options);
}

public class TransportConfiguration
{
    public string Description { get; }

    public TransportConfiguration(string description)
    {
        Description = description;
    }
    
    public Action<WolverineOptions> ConfigureReceiver { get; init; }
    public Action<WolverineOptions> ConfigureSender { get; init; }

    public override string ToString()
    {
        return Description;
    }
}

public abstract class ChaosScript
{
    protected ChaosScript(string description, TimeSpan timeOut)
    {
        Description = description;
        TimeOut = timeOut;
    }

    public string Description { get; }

    public abstract Task Drive(ChaosDriver driver);
    
    public TimeSpan TimeOut { get; }

    public override string ToString()
    {
        return $"{nameof(Description)}: {Description}";
    }
}

public class ChaosDriver : IAsyncDisposable, IDisposable
{
    private readonly IMessageStorageStrategy _storage;
    private readonly TransportConfiguration _transportConfiguration;
    private readonly Dictionary<string, IHost> _senders = new();
    private readonly Dictionary<string, IHost> _receivers = new();


    public ChaosDriver(IMessageStorageStrategy storage, TransportConfiguration transportConfiguration)
    {
        _storage = storage;
        _transportConfiguration = transportConfiguration;
    }



    public async Task InitializeAsync()
    {
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                _storage.ConfigureSenderPersistence(opts);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // Cleans out existing inbox/outbox state as well
        // as clearing out queues
        await sender.ResetResourceState();

        using (var nested = sender.Services.As<IContainer>().GetNestedContainer())
        {
            var storage = nested.GetInstance<IMessageRecordRepository>();
            await storage.ClearMessageRecords();
        }

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                _storage.ConfigureReceiverPersistence(opts);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // Cleans out existing inbox/outbox state as well
        // as clearing out queues
        await receiver.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _senders.Values) await host.StopAsync();

        foreach (var host in _receivers.Values) await host.StopAsync();
    }
    
    public void Dispose()
    {
        foreach (var host in _senders.Values) host.Dispose();

        foreach (var host in _receivers.Values) host.Dispose();
    }

    public void SendMessagesContinuously(string name, int batchSize, TimeSpan duration)
    {
        var endingDate = DateTimeOffset.UtcNow.Add(duration);
        var bus = _senders[name].Services.GetRequiredService<IMessageBus>();
        var task = Task.Factory.StartNew(async () =>
        {
            while (DateTimeOffset.UtcNow < endingDate)
            {
                await bus.PublishAsync(new SendMessages(batchSize));
            }
        });
    }

    public async Task SendMessages(string name, int number)
    {
        var bus = _senders[name].Services.GetRequiredService<IMessageBus>();
        while (number > 0)
        {
            if (number > 100)
            {
                await bus.PublishAsync(new SendMessages(100));
                number -= 100;
            }
            else if (number > 10)
            {
                await bus.PublishAsync(new SendMessages(10));
                number -= 10;
            }
            else
            {
                await bus.PublishAsync(new SendMessages(number));
                number = 0;
            }
        }
    }
    
    // TODO -- check the queue counts
    
    

    public async Task<bool> WaitForAllMessagingToComplete(TimeSpan time)
    {
        var timeout = new CancellationTokenSource(time);

        await WaitForAllSendingToComplete(timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        

        var receiver = _receivers.Values.FirstOrDefault();
        using var nested = receiver.Services.As<IContainer>().GetNestedContainer();
        var repository = nested.GetInstance<IMessageRecordRepository>();

        while (!timeout.IsCancellationRequested)
        {
            var count = await repository.FindOutstandingMessageCount(timeout.Token);
            if (count == 0)
            {
                return true;
            }

            await Task.Delay(100.Milliseconds(), timeout.Token);
        }

        timeout.Token.ThrowIfCancellationRequested();

        return false;
    }

    public async Task WaitForAllSendingToComplete(CancellationToken cancellationToken)
    {
        foreach (var sender in _senders.Values)
        {
            var runtime = sender.Services.GetRequiredService<IWolverineRuntime>();
            var sendingQueue = runtime.Endpoints.LocalQueueForMessageType(typeof(SendMessages));
            while (!cancellationToken.IsCancellationRequested && sendingQueue.MessageCount > 0)
            {
                await Task.Delay(100.Milliseconds(), cancellationToken);
            }
        }
    }

    public async Task<IHost> StartReceiver(string name)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                _storage.ConfigureReceiverPersistence(opts);
                opts.Policies.OnAnyException()
                    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

                opts.Services.AddResourceSetupOnStartup();

                _transportConfiguration.ConfigureReceiver(opts);
            }).StartAsync();

        _receivers[name] = host;

        return host;
    }

    public async Task StopReceiver(string name)
    {
        await _receivers[name].StopAsync();
        _receivers.Remove(name);
    }

    public async Task<IHost> StartSender(string name)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                _storage.ConfigureSenderPersistence(opts);

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<SendMessageHandler>();

                opts.PublishMessage<SendMessages>().ToLocalQueue("SendMessages").MaximumParallelMessages(10);

                _transportConfiguration.ConfigureSender(opts);
            }).StartAsync();

        _senders[name] = host;

        return host;
    }

    public async Task StopSender(string name)
    {
        await _senders[name].StopAsync();
        _senders.Remove(name);
    }
}