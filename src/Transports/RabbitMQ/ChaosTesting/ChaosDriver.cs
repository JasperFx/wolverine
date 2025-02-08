using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit.Abstractions;

namespace ChaosTesting;

public interface IMessageStorageStrategy
{
    void ConfigureReceiverPersistence(WolverineOptions options);
    void ConfigureSenderPersistence(WolverineOptions options);
    Task ClearMessageRecords(IServiceProvider services);
    Task<long> FindOutstandingMessageCount(IServiceProvider container, CancellationToken cancellation);
}

public class TransportConfiguration
{
    public TransportConfiguration(string description)
    {
        Description = description;
    }

    public string Description { get; }

    public Action<WolverineOptions> ConfigureReceiver { get; init; }
    public Action<WolverineOptions> ConfigureSender { get; init; }

    public override string ToString()
    {
        return Description;
    }
}

public abstract class ChaosScript
{
    public TimeSpan TimeOut { get; internal set; } = 60.Seconds();
    public abstract Task Drive(ChaosDriver driver);

    public override string ToString()
    {
        return GetType().Name;
    }
}

public class ChaosDriver : IAsyncDisposable, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Dictionary<string, IHost> _receivers = new();
    private readonly Dictionary<string, IHost> _senders = new();
    private readonly IMessageStorageStrategy _storage;
    private readonly TransportConfiguration _transportConfiguration;


    public ChaosDriver(ITestOutputHelper output, IMessageStorageStrategy storage,
        TransportConfiguration transportConfiguration)
    {
        _output = output;
        _storage = storage;
        _transportConfiguration = transportConfiguration;
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

        await _storage.ClearMessageRecords((IServiceProvider)sender.Services);

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                _storage.ConfigureReceiverPersistence(opts);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // Cleans out existing inbox/outbox state as well
        // as clearing out queues
        await receiver.ResetResourceState();

        _output.WriteLine("Cleared out all existing state");
    }

    public void SendMessagesContinuously(string name, int batchSize, TimeSpan duration)
    {
        var endingDate = DateTimeOffset.UtcNow.Add(duration);
        var bus = _senders[name].Services.GetRequiredService<IMessageBus>();
        var random = new Random().Next(0, 10);
        if (random > 8)
        {
            bus.TenantId = "tenant1";
        }
        else if (random > 4)
        {
            bus.TenantId = "tenant2";
        }
        else
        {
            bus.TenantId = "tenant3";
        }

        var task = Task.Factory.StartNew(async () =>
        {
            _output.WriteLine($"Starting to continuously send messages from node {name} in batches of {batchSize}");
            while (DateTimeOffset.UtcNow < endingDate)
            {
                await bus.PublishAsync(new SendMessages(batchSize));
                await Task.Delay(100.Milliseconds());
            }

            _output.WriteLine($"Stopping the continuous sending of messages from node {name}");
        });
    }

    public async Task SendMessages(string name, int number)
    {
        var original = number;
        var bus = _senders[name].Services.GetRequiredService<IMessageBus>();
        var random = new Random().Next(0, 10);
        if (random > 8)
        {
            bus.TenantId = "tenant1";
        }
        else if (random > 4)
        {
            bus.TenantId = "tenant2";
        }
        else
        {
            bus.TenantId = "tenant3";
        }

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

        _output.WriteLine($"Sent {original} messages from node {name}");
    }

    public async Task<bool> WaitForAllMessagingToComplete(TimeSpan time)
    {
        await WaitForAllSendingToComplete(time);

        var receiver = _receivers.Values.FirstOrDefault();

        var count = await _storage.FindOutstandingMessageCount(receiver.Services, CancellationToken.None);
        var attempts = 0;

        while (attempts < 20)
        {
            var newCount = await _storage.FindOutstandingMessageCount(receiver.Services, CancellationToken.None);
            if (newCount == 0)
            {
                _output.WriteLine("Reached zero outstanding messages!");
                return true;
            }

            if (newCount < count)
            {
                attempts = 0;
                _output.WriteLine($"Current outstanding message count is {newCount}");
            }
            else
            {
                attempts++;

                if (attempts >= 5)
                {
                    _output.WriteLine("The test appears to be stalled.");
                    var host = _receivers.Values.FirstOrDefault();
                    if (host != null)
                    {
                        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
                        var queues = runtime.Endpoints.ActiveListeners().Select(x => x.Endpoint).OfType<IBrokerQueue>();
                        foreach (var queue in queues)
                        {
                            var data = await queue.GetAttributesAsync();
                            _output.WriteLine($"Queue {queue.Uri} has {data["count"]} messages");
                        }
                    }
                }
            }

            count = newCount;

            await Task.Delay(200.Milliseconds());
        }

        if (count > 0)
        {
            _output.WriteLine($"Stuck at {count} messages!");
        }

        return false;
    }

    public async Task WaitForAllSendingToComplete(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(timeout);

        foreach (var sender in _senders.Values)
        {
            var runtime = sender.Services.GetRequiredService<IWolverineRuntime>();
            var sendingQueue = runtime.Endpoints.LocalQueueForMessageType(typeof(SendMessages));
            while (!cancellation.Token.IsCancellationRequested && sendingQueue.MessageCount > 0)
            {
                await Task.Delay(100.Milliseconds(), cancellation.Token);
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

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                _transportConfiguration.ConfigureReceiver(opts);
            }).StartAsync();

        _receivers[name] = host;

        _output.WriteLine($"Started receiver {name}");

        return host;
    }

    public async Task StopReceiver(string name)
    {
        await _receivers[name].StopAsync();
        _receivers.Remove(name);

        _output.WriteLine($"Stopped receiver {name}");
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

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                _transportConfiguration.ConfigureSender(opts);
            }).StartAsync();

        _senders[name] = host;

        _output.WriteLine($"Started sender {name}");

        return host;
    }

    public async Task StopSender(string name)
    {
        await _senders[name].StopAsync();
        _senders.Remove(name);

        _output.WriteLine($"Stopped sender {name}");
    }
}