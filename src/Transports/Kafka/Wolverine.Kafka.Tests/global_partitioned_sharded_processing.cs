using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using Marten.Metadata;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class global_partitioned_sharded_processing
{
    private readonly ITestOutputHelper _output;

    public global_partitioned_sharded_processing(ITestOutputHelper output)
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

                    await bus.PublishAsync(new GLogA(id));
                    await bus.PublishAsync(new GLogB(id));
                    await bus.PublishAsync(new GLogC(id));
                    await bus.PublishAsync(new GLogD(id));
                    await bus.PublishAsync(new GLogD(id));
                    await bus.PublishAsync(new GLogC(id));
                    await bus.PublishAsync(new GLogB(id));
                    await bus.PublishAsync(new GLogA(id));
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task hammer_it_with_lots_of_messages_global_partitioned()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseKafka("localhost:9092").AutoProvision();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(GLetterMessageHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gletters_kafka";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.MessagePartitioning.ByMessage<IGLetterMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedKafkaTopics("gletters", 4);
                    topology.MessagesImplementing<IGLetterMessage>();
                });
            }).StartAsync();

        var tracked = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(120.Seconds())
            .ExecuteAndWaitAsync(pumpOutMessages);

        var envelopes = tracked.Executed.Envelopes().ToArray();

        var counts = envelopes.GroupBy(x => x.Destination);
        foreach (var count in counts)
        {
            _output.WriteLine(count.Key.ToString() + " had " + count.Count());
        }

        // In single-node mode, global partitioning routes directly to companion local queues
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters1/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters2/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters3/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters4/")).ShouldBeTrue();
    }
}

public interface IGLetterMessage
{
    Guid Id { get; }
}

public record GLogA(Guid Id) : IGLetterMessage;
public record GLogB(Guid Id) : IGLetterMessage;
public record GLogC(Guid Id) : IGLetterMessage;
public record GLogD(Guid Id) : IGLetterMessage;

[AggregateHandler]
public static class GLetterMessageHandler
{
    public static GAEvent Handle(GLogA command, GSimpleAggregate aggregate, Envelope envelope)
    {
        Debug.WriteLine($"Got GLogA for {command.Id} at envelope {envelope.Destination}");
        return new GAEvent();
    }

    public static GBEvent Handle(GLogB command, GSimpleAggregate aggregate, Envelope envelope)
    {
        Debug.WriteLine($"Got GLogB for {command.Id} at envelope {envelope.Destination}");
        return new GBEvent();
    }

    public static GCEvent Handle(GLogC command, GSimpleAggregate aggregate, Envelope envelope)
    {
        Debug.WriteLine($"Got GLogC for {command.Id} at envelope {envelope.Destination}");
        return new GCEvent();
    }

    public static GDEvent Handle(GLogD command, GSimpleAggregate aggregate, Envelope envelope)
    {
        Debug.WriteLine($"Got GLogD for {command.Id} at envelope {envelope.Destination}");
        return new GDEvent();
    }
}

public class GSimpleAggregate : IRevisioned
{
    public int Version { get; set; }
    public Guid Id { get; set; }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }

    public void Apply(GAEvent _) => ACount++;
    public void Apply(GBEvent _) => BCount++;
    public void Apply(GCEvent _) => CCount++;
    public void Apply(GDEvent _) => DCount++;
}

public record GAEvent;
public record GBEvent;
public record GCEvent;
public record GDEvent;
