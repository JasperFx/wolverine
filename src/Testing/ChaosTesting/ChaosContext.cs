using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Oakton.Resources;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

namespace ChaosTesting;

public class ChaosContext : IAsyncDisposable
{
    private readonly Dictionary<string, IHost> _senders = new();
    private readonly Dictionary<string, IHost> _receivers = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _senders.Values)
        {
            await host.StopAsync();
        }

        foreach (var host in _receivers.Values)
        {
            await host.StopAsync();
        }
    }

    public async Task StartAsync()
    {
        using var store = DocumentStore.For(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "chaos";
        });

        await store.Advanced.Clean.CompletelyRemoveAllAsync();

        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("chaos_receiver");
        await conn.DropSchemaAsync("chaos_sender");
    }

    public Action<RabbitMqMessageRoutingConvention> RabbitConfig { get; set; } = c => { };

    public Task SendMessagesContinuously(string name, int batchSize, TimeSpan duration)
    {
        var endingDate = DateTimeOffset.UtcNow.Add(duration);
        var bus = _senders[name].Services.GetRequiredService<IMessageBus>();
        return Task.Factory.StartNew(async () =>
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
            else
            {
                await bus.PublishAsync(new SendMessages(number));
            }
        }
    }

    public async Task<IHost> StartReceiver(string name)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "chaos";
                }).IntegrateWithWolverine("chaos_receiver");
                
                opts.Policies.AutoApplyTransactions();
                opts.Policies.OnAnyException()
                    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

                opts.Services.AddResourceSetupOnStartup();

                opts.UseRabbitMq().UseConventionalRouting(RabbitConfig);
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
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "chaos";
                }).IntegrateWithWolverine("chaos_sender");
                
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<SendMessageHandler>();

                opts.UseRabbitMq().UseConventionalRouting(RabbitConfig);
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

