using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using MartenTests.AggregateHandlerWorkflow;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

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
                opts.MessagePartitioning.ByMessage<ILetterMessage>(x => x.Id.ToString());
                
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.MessagePartitioning.PublishToShardedLocalMessaging("letters", 4, topology =>
                {
                    topology.MessagesImplementing<ILetterMessage>();
                    topology.MaxDegreeOfParallelism = ShardSlots.Five;
                    
                    topology.ConfigureQueues(queue =>
                    {
                        queue.BufferedInMemory();
                    });
                });
            }).StartAsync();

        // Re-purposing the test a bit. Making sure we're constructing forwarding correctly
        var executor = host.GetRuntime().As<IExecutorFactory>().BuildFor(typeof(LogA), new StubEndpoint("Wrong", new StubTransport()));
        executor.As<Executor>().Handler.ShouldBeOfType<PartitionedMessageReRouter>()
            .MessageType.ShouldBe(typeof(LogA));

        var tracked = await host.ExecuteAndWaitAsync(pumpOutMessages, 60000);
        
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("local://letters1")).ShouldBeTrue();
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("local://letters2")).ShouldBeTrue();
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("local://letters3")).ShouldBeTrue();
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("local://letters4")).ShouldBeTrue();
    }
    
    [Fact]
    public async Task hammer_it_with_lots_of_messages_against_buffered_and_sharded_messaging()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
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

                opts.MessagePartitioning.PublishToShardedLocalMessaging("letters", 4, topology =>
                {
                    topology.MessagesImplementing<ILetterMessage>();
                    topology.MaxDegreeOfParallelism = ShardSlots.Five;
                    
                    topology.ConfigureQueues(queue =>
                    {
                        queue.UseDurableInbox();
                    });
                });
            }).StartAsync();

        // This is just pumping out a ton of messages of different types of ILetterMessage
        // that simulate getting a burst of messages that all append events to Marten streams
        // w/ the same stream id
        // w/o the "sharded" message routing and execution above, this test falls over fast w/
        // Marten detecting ConcurrencyExceptions right and left
        var tracked = await host.ExecuteAndWaitAsync(pumpOutMessages, 60000);
        
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("local://letters1")).ShouldBeTrue();
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("local://letters2")).ShouldBeTrue();
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("local://letters3")).ShouldBeTrue();
        tracked.Executed.Envelopes().Any(x => x.Destination == new Uri("local://letters4")).ShouldBeTrue();
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
                opts.MessagePartitioning.ByMessage<ILetterMessage>(x => x.Id.ToString());
                
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