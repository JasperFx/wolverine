
using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Wolverine.AmazonSqs.Tests;

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
    
    [Fact] 
    public async Task hammer_it_with_lots_of_messages_against_buffered()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseAmazonSqsTransportLocally().AutoProvision().AutoPurgeOnStartup();
                
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

                opts.MessagePartitioning.PublishToShardedAmazonSqsQueues("letters", 4, topology =>
                {
                    topology.MessagesImplementing<ILetterMessage>();
                    topology.MaxDegreeOfParallelism = ShardSlots.Five;
                    
                    topology.ConfigureListening(x => x.BufferedInMemory().MessageBatchSize(10));

                });
            }).StartAsync();

        var tracked = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .ExecuteAndWaitAsync(pumpOutMessages);

        var envelopes = tracked.Executed.Envelopes().ToArray();

        var counts = envelopes.GroupBy(x => x.Destination);
        foreach (var count in counts)
        {
            _output.WriteLine(count.Key.ToString() + " had " + count.Count());
        }
        
        envelopes.Any(x => x.Destination == new Uri("sqs://letters1")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("sqs://letters2")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("sqs://letters3")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("sqs://letters4")).ShouldBeTrue();
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