
using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Xunit;
namespace Wolverine.AmazonSqs.Tests;

public class concurrency_resilient_sharded_processing
{
    private readonly ITestOutputHelper _output;

    public concurrency_resilient_sharded_processing(ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task pumpOutMessages(IMessageContext bus, Guid[] ids)
    {
        var tasks = new Task[5];
        for (int i = 0; i < tasks.Length; i++)
        {
            var batch = i;
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < 5; j++)
                {
                    var id = ids[batch * 5 + j];

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
                

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.ListenToSqsQueue("from_external");

                #region sample_partitioned_publishing_through_amazon_sqs
                // Telling Wolverine how to assign a GroupId to a message, that we'll use
                // to predictably sort into "slots" in the processing
                opts.MessagePartitioning.ByMessage<ILetterMessage>(x => x.Id.ToString());
                
                opts.MessagePartitioning.PublishToShardedAmazonSqsQueues("letters", 4, topology =>
                {
                    topology.MessagesImplementing<ILetterMessage>();
                    topology.MaxDegreeOfParallelism = PartitionSlots.Five;
                    
                    topology.ConfigureListening(x => x.BufferedInMemory().MessageBatchSize(10));

                });

                #endregion
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = ShardedLetterIds.CoveringEveryShard(host.GetRuntime().Options.MessagePartitioning, 4, 25);

        var tracked = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(120.Seconds())
            .ExecuteAndWaitAsync(c => pumpOutMessages(c, ids));

        var envelopes = tracked.Executed.Envelopes().ToArray();

        var counts = envelopes.GroupBy(x => x.Destination);
        foreach (var count in counts)
        {
            _output.WriteLine(count.Key!.ToString() + " had " + count.Count());
        }
        
        envelopes.Any(x => x.Destination == new Uri("sqs://letters1")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("sqs://letters2")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("sqs://letters3")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("sqs://letters4")).ShouldBeTrue();
    }

}


/// <summary>
/// GH-5736. The sharded tests assert that every shard executed something, which only holds if the group ids they
/// publish actually land on every shard. Twenty-five random Guids miss one of four shards about 0.3% of the time --
/// (3/4)^25 for each shard -- and that is how hammer_it_with_lots_of_messages_against_buffered failed once with
/// "sqs://letters4" never receiving anything, while every message that WAS sent executed. Choosing the ids with the
/// same slot calculation the sharded route uses keeps the assertion and removes the dice roll.
/// </summary>
internal static class ShardedLetterIds
{
    public static Guid[] CoveringEveryShard(MessagePartitioningRules rules, int numberOfShards, int count)
    {
        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            var shard = i % numberOfShards;
            Guid id;
            do
            {
                id = Guid.NewGuid();
            } while (new Envelope(new LogA(id)).SlotForSending(numberOfShards, rules) != shard);

            ids[i] = id;
        }

        return ids;
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

public partial class SimpleAggregate : IRevisioned
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