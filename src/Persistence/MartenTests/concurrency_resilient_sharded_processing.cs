using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Marten;
using MartenTests.AggregateHandlerWorkflow;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

public class concurrency_resilient_sharded_processing
{
    private async Task pumpOutMessages(IMessageContext bus)
    {
        var tasks = new Task[10];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < 20; j++)
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
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LetterMessageHandler));
                
                // Telling Wolverine how to assign a GroupId to a message, that we'll use
                // to predictably sort into "slots" in the processing
                opts.MessageGrouping.ByMessage<ILetterMessage>(x => x.Id.ToString());
                
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Publish(x =>
                {
                    x.MessagesImplementing<ILetterMessage>();
                    x.ToLocalQueue("letters")
                        
                        // This is the magic sauce that shards the processing
                        // by GroupId, which would be the StreamId.ToString() in
                        // most cases in your usage
                        .ShardListeningByGroupId(ShardSlots.Five)
                        
                        .BufferedInMemory()
                        .MaximumParallelMessages(10);
                });
            }).StartAsync();

        var tracked = await host.ExecuteAndWaitAsync(pumpOutMessages, 60000);
    }
    
    [Fact]
    public async Task hammer_it_with_lots_of_messages_against_durable()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LetterMessageHandler));
                
                // Telling Wolverine how to assign a GroupId to a message, that we'll use
                // to predictably sort into "slots" in the processing
                opts.MessageGrouping.ByMessage<ILetterMessage>(x => x.Id.ToString());
                
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Publish(x =>
                {
                    x.MessagesImplementing<ILetterMessage>();
                    x.ToLocalQueue("letters")
                        
                        // This is the magic sauce that shards the processing
                        // by GroupId, which would be the StreamId.ToString() in
                        // most cases in your usage
                        .ShardListeningByGroupId(ShardSlots.Five)
                        
                        .UseDurableInbox()
                        .MaximumParallelMessages(10);
                });
            }).StartAsync();

        var tracked = await host.ExecuteAndWaitAsync(pumpOutMessages, 60000);
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
    public static AEvent Handle(LogA command, SimpleAggregate aggregate) => new AEvent();
    public static BEvent Handle(LogB command, SimpleAggregate aggregate) => new BEvent();
    public static CEvent Handle(LogC command, SimpleAggregate aggregate) => new CEvent();
    public static DEvent Handle(LogD command, SimpleAggregate aggregate) => new DEvent();
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