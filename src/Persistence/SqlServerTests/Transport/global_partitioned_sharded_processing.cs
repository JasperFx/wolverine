using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;

namespace SqlServerTests.Transport;

public class global_partitioned_sharded_processing : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        SqlLetterHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "gletters_sql",
                        transportSchema: "gletters_sql_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SqlLetterHandler));

                opts.MessagePartitioning.ByMessage<ISqlLetterMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedSqlServerQueues("gletters", 4);
                    topology.MessagesImplementing<ISqlLetterMessage>();
                });
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static async Task pumpOutMessages(IMessageContext bus)
    {
        var tasks = new Task[5];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                for (var j = 0; j < 5; j++)
                {
                    var id = Guid.NewGuid();

                    await bus.PublishAsync(new SqlLogA(id));
                    await bus.PublishAsync(new SqlLogB(id));
                    await bus.PublishAsync(new SqlLogC(id));
                    await bus.PublishAsync(new SqlLogD(id));
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void builds_the_expected_shard_queues()
    {
        var transport = _host.GetRuntime().Options.Transports.OfType<SqlServerTransport>().Single();

        foreach (var name in new[] { "gletters1", "gletters2", "gletters3", "gletters4" })
        {
            var queue = transport.Queues[name];
            queue.UsedInShardedTopology.ShouldBeTrue();
            queue.IsListener.ShouldBeTrue();
            queue.ListenerScope.ShouldBe(ListenerScope.Exclusive);

            // Global partitioning forces durable mode on every slot
            queue.Mode.ShouldBe(EndpointMode.Durable);

            // Each slot is tagged with its companion local queue
            queue.GlobalPartitionLocalQueueUri.ShouldNotBeNull();

            // Sharded queues opt into the seq-clustered FIFO layout by default (GH-3469)
            queue.OptimizeThroughput.ShouldBeTrue();
            queue.QueueTable.Columns.Any(x => x.Name == "seq").ShouldBeTrue();
            queue.ScheduledTable.Columns.Any(x => x.Name == "seq").ShouldBeTrue();
        }

        // ...and the opt-in is per queue, so the transport-wide default is untouched
        transport.OptimizeQueueThroughput.ShouldBeFalse();
    }

    [Fact]
    public async Task hammer_it_with_lots_of_messages_global_partitioned()
    {
        var tracked = await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(120.Seconds())
            .ExecuteAndWaitAsync(pumpOutMessages);

        var envelopes = tracked.Executed.Envelopes().ToArray();

        // In single-node mode, global partitioning routes directly to companion local queues
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters1/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters2/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters3/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters4/")).ShouldBeTrue();

        // Every message for one group id lands on exactly one slot
        foreach (var group in SqlLetterHandler.Received.GroupBy(x => x.Id))
        {
            group.Select(x => x.Destination).Distinct().Count().ShouldBe(1);
        }
    }
}

/// <summary>
/// The publishing-only sibling of the topology above. This one has no companion local queue
/// shortcut, so every message really does round-trip through the sharded SQL Server queue tables.
/// </summary>
public class sharded_publishing_through_sqlserver_queues : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        SqlLetterHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "pletters_sql",
                        transportSchema: "pletters_sql_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SqlLetterHandler));

                opts.MessagePartitioning.ByMessage<ISqlLetterMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.PublishToShardedSqlServerQueues("pletters", 4, topology =>
                {
                    topology.MessagesImplementing<ISqlLetterMessage>();
                    topology.MaxDegreeOfParallelism = PartitionSlots.Five;
                });
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static async Task publishOneRoundOfLetters(IMessageContext bus)
    {
        for (var i = 0; i < 25; i++)
        {
            var id = Guid.NewGuid();
            await bus.PublishAsync(new SqlLogA(id));
            await bus.PublishAsync(new SqlLogB(id));
            await bus.PublishAsync(new SqlLogC(id));
            await bus.PublishAsync(new SqlLogD(id));
        }
    }

    [Fact]
    public async Task messages_round_trip_through_every_shard_queue()
    {
        var tracked = await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(120.Seconds())
            .ExecuteAndWaitAsync(publishOneRoundOfLetters);

        var envelopes = tracked.Executed.Envelopes().ToArray();

        envelopes.Any(x => x.Destination == new Uri("sqlserver://pletters1")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("sqlserver://pletters2")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("sqlserver://pletters3")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("sqlserver://pletters4")).ShouldBeTrue();

        // Every message carrying one group id lands on exactly one shard queue
        foreach (var group in SqlLetterHandler.Received.GroupBy(x => x.Id))
        {
            group.Select(x => x.Destination).Distinct().Count().ShouldBe(1);
        }
    }
}

public interface ISqlLetterMessage
{
    Guid Id { get; }
}

public record SqlLogA(Guid Id) : ISqlLetterMessage;

public record SqlLogB(Guid Id) : ISqlLetterMessage;

public record SqlLogC(Guid Id) : ISqlLetterMessage;

public record SqlLogD(Guid Id) : ISqlLetterMessage;

public static class SqlLetterHandler
{
    public static readonly ConcurrentBag<(Guid Id, Uri? Destination)> Received = new();

    public static void Handle(SqlLogA message, Envelope envelope) => Received.Add((message.Id, envelope.Destination));
    public static void Handle(SqlLogB message, Envelope envelope) => Received.Add((message.Id, envelope.Destination));
    public static void Handle(SqlLogC message, Envelope envelope) => Received.Add((message.Id, envelope.Destination));
    public static void Handle(SqlLogD message, Envelope envelope) => Received.Add((message.Id, envelope.Destination));
}
