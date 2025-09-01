using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

public class concurrency_resilient_sharded_processing
{
    private readonly ITestOutputHelper _output;

    public concurrency_resilient_sharded_processing(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task pumpOutMessages(IMessageContext bus)
    {
        var tasks = new Task[5];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < 5; j++)
                {
                    var id = Guid.NewGuid();

                    await bus.PublishAsync(new LogA(id));
                    await bus.PublishAsync(new LogB(id));
                    await bus.PublishAsync(new LogC(id));
                    await bus.PublishAsync(new LogD(id));
                    await bus.PublishAsync(new LogD(id));
                    await bus.PublishAsync(new LogC(id));
                    await bus.PublishAsync(new LogB(id));
                    await bus.PublishAsync(new LogA(id));
                }
            });
        }

        await Task.WhenAll(tasks);
    }
    
    //[Fact] --- it works, just times out very easily
    public async Task hammer_it_with_lots_of_messages_against_buffered()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LetterMessageHandler));
                
                // Telling Wolverine how to assign a GroupId to a message, that we'll use
                // to predictably sort into "slots" in the processing
                opts.MessagePartitioning.ByMessage<ILetterMessage>(x => x.Id.ToString());
                
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.MessagePartitioning.PublishToShardedRabbitQueues("letters", 4, topology =>
                {
                    topology.MessagesImplementing<ILetterMessage>();
                    topology.MaxDegreeOfParallelism = ShardSlots.Five;
                    
                    topology.ConfigureListening(x => x.BufferedInMemory());

                });
            }).StartAsync();



        var tracked = await host.ExecuteAndWaitAsync(pumpOutMessages, 60000);

        var envelopes = tracked.Executed.Envelopes().ToArray();

        var counts = envelopes.GroupBy(x => x.Destination);
        foreach (var count in counts)
        {
            _output.WriteLine(count.Key.ToString() + " had " + count.Count());
        }
        
        envelopes.Any(x => x.Destination == new Uri("rabbitmq://queue/letters1")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("rabbitmq://queue/letters2")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("rabbitmq://queue/letters3")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("rabbitmq://queue/letters4")).ShouldBeTrue();
    }
    
    //[Fact]
    public async Task hammer_it_with_lots_of_messages_against_buffered_when_resent()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LetterMessageHandler));

                opts.ListenToRabbitQueue("external_system").Named("external");
                
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                // Telling Wolverine how to assign a GroupId to a message, that we'll use
                // to predictably sort into "slots" in the processing
                opts
                    .MessagePartitioning.ByMessage<ILetterMessage>(x => x.Id.ToString())
                    .PublishToShardedRabbitQueues("letters", 4, topology =>
                {
                    topology.MessagesImplementing<ILetterMessage>();
                    topology.MaxDegreeOfParallelism = ShardSlots.Five;
                    
                    topology.ConfigureListening(x => x.BufferedInMemory());

                });
            }).StartAsync();


        var tracked = await host.ExecuteAndWaitAsync(async bus =>
        {
            var endpoint = bus.EndpointFor("external");
            var tasks = new Task[3];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    for (int j = 0; j < 5; j++)
                    {
                        var id = Guid.NewGuid();

                        await endpoint.SendAsync(new LogA(id));
                        await endpoint.SendAsync(new LogB(id));
                        await endpoint.SendAsync(new LogC(id));
                        await endpoint.SendAsync(new LogD(id));
                        await endpoint.SendAsync(new LogD(id));
                        await endpoint.SendAsync(new LogC(id));
                        await endpoint.SendAsync(new LogB(id));
                        await endpoint.SendAsync(new LogA(id));
                    }
                });
            }

            await Task.WhenAll(tasks);
        }, 300000);

        var envelopes = tracked.Executed.Envelopes().ToArray();

        var counts = envelopes.GroupBy(x => x.Destination);
        foreach (var count in counts)
        {
            _output.WriteLine(count.Key.ToString() + " had " + count.Count());
        }
        
        envelopes.Any(x => x.Destination == new Uri("rabbitmq://queue/letters1")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("rabbitmq://queue/letters2")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("rabbitmq://queue/letters3")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("rabbitmq://queue/letters4")).ShouldBeTrue();
    }

    //[Fact]
    public async Task hammer_it_with_lots_of_messages_against_durable()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LetterMessageHandler));
                
                // Telling Wolverine how to assign a GroupId to a message, that we'll use
                // to predictably sort into "slots" in the processing
                opts.MessagePartitioning.ByMessage<ILetterMessage>(x => x.Id.ToString());
                
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.MessagePartitioning.PublishToShardedRabbitQueues("letters", 4, topology =>
                {
                    topology.MessagesImplementing<ILetterMessage>();
                    topology.MaxDegreeOfParallelism = ShardSlots.Five;
                    
                    topology.ConfigureListening(x => x.UseDurableInbox());

                });
            }).StartAsync();

        var routes = host.GetRuntime().RoutingFor(typeof(LogA));
        

        var tracked = await host.ExecuteAndWaitAsync(pumpOutMessages, 60000);
        
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("rabbitmq://queue/letters1")).ShouldBeTrue();
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("rabbitmq://queue/letters2")).ShouldBeTrue();
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("rabbitmq://queue/letters3")).ShouldBeTrue();
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("rabbitmq://queue/letters4")).ShouldBeTrue();
    }

}


public interface ILetterMessage
{
    Guid Id { get; }
}

public record LogA(Guid Id) : ILetterMessage;
public record LogB(Guid Id) : ILetterMessage;
public record LogC(Guid Id) : ILetterMessage;
public record LogD(Guid Id) : ILetterMessage;



[AggregateHandler]
public static class LetterMessageHandler
{
    public static AEvent Handle(LogA command, SimpleAggregate aggregate, Envelope envelope)
    {
        Debug.WriteLine($"Got LogA for {command.Id} at envelope {envelope.Destination}");   
        
        return new AEvent();
    }
    
    public static BEvent Handle(LogB command, SimpleAggregate aggregate, Envelope envelope)
    {
        Debug.WriteLine($"Got LogB for {command.Id} at envelope {envelope.Destination}");   
        
        return new BEvent();
    }
    
    public static CEvent Handle(LogC command, SimpleAggregate aggregate, Envelope envelope)
    {
        Debug.WriteLine($"Got LogC for {command.Id} at envelope {envelope.Destination}");   
        
        return new CEvent();
    }
    
    public static DEvent Handle(LogD command, SimpleAggregate aggregate, Envelope envelope)
    {
        Debug.WriteLine($"Got LogD for {command.Id} at envelope {envelope.Destination}");   
        
        return new DEvent();
    }

}

public class SimpleAggregate : IRevisioned
{
    // This will be the aggregate version
    public int Version { get; set; }

    public Guid Id { get;
        set; }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
    public int ECount { get; set; }

    public void Apply(AEvent _)
    {
        ACount++;
    }

    public void Apply(BEvent _)
    {
        BCount++;
    }

    public void Apply(CEvent _)
    {
        CCount++;
    }

    public void Apply(DEvent _)
    {
        DCount++;
    }


}

public record AEvent;

public record BEvent;

public record CEvent;

public record DEvent;