using System.Collections.Concurrent;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

// Postgres if true, Rabbit if false
var usePostgres = false;

await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        if (usePostgres)
        {
            opts.PublishMessage<Ping>().ToPostgresqlQueue("ping_queue");
            opts.PublishMessage<Pong>().ToPostgresqlQueue("pong_queue");
            opts.ListenToPostgresqlQueue("ping_queue");
            opts.ListenToPostgresqlQueue("pong_queue");
        }
        else
        {
            opts.UseRabbitMq(new Uri("amqp://localhost"));
            opts.PublishMessage<Ping>().ToRabbitQueue("ping_queue");
            opts.PublishMessage<Pong>().ToRabbitQueue("pong_queue");
            opts.ListenToRabbitQueue("ping_queue")
                .PreFetchCount(10)
                .ListenerCount(5)
                .UseDurableInbox(); // Remove durable inbox and the problem goes away
            opts.ListenToRabbitQueue("pong_queue")
                .PreFetchCount(10)
                .ListenerCount(5)
                .ProcessInline();
        }

        opts.Services.AddResourceSetupOnStartup();

        opts.Durability.Mode = DurabilityMode.Solo;
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService<Sender>();

        #region sample_using_integrate_with_wolverine_with_multiple_options

        services.AddMarten(opts =>
            {
                opts.Connection(Servers.PostgresConnectionString);
                opts.DisableNpgsqlLogging = true;
            })
            .IntegrateWithWolverine(w =>
            {
                w.MessageStorageSchemaName = "public";
                w.TransportSchemaName = "public";
            })
            .ApplyAllDatabaseChangesOnStartup();

        #endregion
    })
    .UseResourceSetupOnStartup()
    .RunJasperFxCommands(args);


public class Sender(ILogger<Sender> logger, IMessageBus bus) : BackgroundService
{
    static ConcurrentDictionary<string, bool> _sentMessages = new();
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var uid = Random.Shared.Next(0, 1_000_000); // to avoid any previous message from an earlier run to interfere with this run
        for (var i = 0; i < 200; i++)
        {
            var pingNumber = $"{uid} - {i}";

            if (!_sentMessages.TryAdd(pingNumber, true))
            {
                logger.LogError("DUPLICATE PING GOING OUT WITH NUMBER " + pingNumber);
            }
            
            logger.LogInformation("Sending Ping #{Number}", pingNumber);
            await bus.PublishAsync(new Ping(pingNumber));
        }
    }
}


public class MessageHandler
{
    private static ConcurrentDictionary<string, int> _receivedMessages = new();
    

    public Pong Handle(Ping ping, ILogger<MessageHandler> logger, Envelope envelope)
    {
        //_tracker.Track(envelope);
        
        logger.LogInformation("Received Ping #{Number}", ping.Id);
        Thread.Sleep(1000); // await Task.Delay(1000); produces same result. I use Thread.Sleep to exclude potential async/await issues  
        return new Pong(ping.Id);
    }

    public static void Handle(Pong pong, ILogger<MessageHandler> logger, Envelope envelope)
    {
        if (_receivedMessages.TryAdd(pong.Id, 1))
        {
            logger.LogInformation("Received Pong #{Number}", pong.Id);
        }
        else
        {
            logger.LogError("Received Pong #{Number} as Duplicate with Id {Id} and attempt {Attempt}", pong.Id, envelope.Id, envelope.Attempts);
        }
    }
}

public record Ping(string Id);

public record Pong(string Id);
