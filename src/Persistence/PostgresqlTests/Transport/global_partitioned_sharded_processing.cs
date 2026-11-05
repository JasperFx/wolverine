using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

public class global_partitioned_sharded_processing : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        PgLetterHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "gletters_pg",
                        transportSchema: "gletters_pg_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PgLetterHandler));

                opts.MessagePartitioning.ByMessage<IPgLetterMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues("gletters", 4);
                    topology.MessagesImplementing<IPgLetterMessage>();
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

                    await bus.PublishAsync(new PgLogA(id));
                    await bus.PublishAsync(new PgLogB(id));
                    await bus.PublishAsync(new PgLogC(id));
                    await bus.PublishAsync(new PgLogD(id));
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void builds_the_expected_shard_queues()
    {
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>();

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
        }
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
        foreach (var group in PgLetterHandler.Received.GroupBy(x => x.Id))
        {
            group.Select(x => x.Destination).Distinct().Count().ShouldBe(1);
        }
    }
}

/// <summary>
/// The publishing-only sibling of the topology above. This one has no companion local queue
/// shortcut, so every message really does round-trip through the sharded PostgreSQL queue tables.
/// </summary>
public class sharded_publishing_through_postgresql_queues : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        PgLetterHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "pletters_pg",
                        transportSchema: "pletters_pg_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PgLetterHandler));

                opts.MessagePartitioning.ByMessage<IPgLetterMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.PublishToShardedPostgresqlQueues("pletters", 4, topology =>
                {
                    topology.MessagesImplementing<IPgLetterMessage>();
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
            await bus.PublishAsync(new PgLogA(id));
            await bus.PublishAsync(new PgLogB(id));
            await bus.PublishAsync(new PgLogC(id));
            await bus.PublishAsync(new PgLogD(id));
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

        envelopes.Any(x => x.Destination == new Uri("postgresql://pletters1")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("postgresql://pletters2")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("postgresql://pletters3")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("postgresql://pletters4")).ShouldBeTrue();

        // Every message carrying one group id lands on exactly one shard queue
        foreach (var group in PgLetterHandler.Received.GroupBy(x => x.Id))
        {
            group.Select(x => x.Destination).Distinct().Count().ShouldBe(1);
        }
    }
}

public interface IPgLetterMessage
{
    Guid Id { get; }
}

public record PgLogA(Guid Id) : IPgLetterMessage;

public record PgLogB(Guid Id) : IPgLetterMessage;

public record PgLogC(Guid Id) : IPgLetterMessage;

public record PgLogD(Guid Id) : IPgLetterMessage;

public static class PgLetterHandler
{
    public static readonly ConcurrentBag<(Guid Id, Uri? Destination)> Received = new();

    public static void Handle(PgLogA message, Envelope envelope) => Received.Add((message.Id, envelope.Destination));
    public static void Handle(PgLogB message, Envelope envelope) => Received.Add((message.Id, envelope.Destination));
    public static void Handle(PgLogC message, Envelope envelope) => Received.Add((message.Id, envelope.Destination));
    public static void Handle(PgLogD message, Envelope envelope) => Received.Add((message.Id, envelope.Destination));
}

/// <summary>
/// GH-5088: the publishing application must not listen to the shard queues, so a separate
/// application can consume them later.
/// </summary>
public class publish_only_to_sharded_postgresql_queues : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "pletters_pub_only",
                        transportSchema: "pletters_pub_only_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                // Deliberately no handler for the messages: this node only publishes
                opts.Discovery.DisableConventionalDiscovery();

                opts.MessagePartitioning.ByMessage<IPgLetterMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.PublishOnlyToShardedPostgresqlQueues("plettersonly", 4, topology =>
                {
                    topology.MessagesImplementing<IPgLetterMessage>();
                    topology.MaxDegreeOfParallelism = PartitionSlots.Five;
                });
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_shard_queues_are_not_listened_to()
    {
        var runtime = _host.GetRuntime();
        var queues = runtime.Options.PostgresqlTransport().Queues
            .Where(x => x.EndpointName.StartsWith("plettersonly")).ToArray();

        queues.Length.ShouldBe(4);
        queues.ShouldAllBe(x => !x.IsListener);
    }

    [Fact]
    public async Task published_messages_stay_in_the_queue_tables_unprocessed()
    {
        var bus = _host.MessageBus();
        for (var i = 0; i < 10; i++)
        {
            await bus.PublishAsync(new PgLogA(Guid.NewGuid()));
        }

        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        var transport = _host.GetRuntime().Options.PostgresqlTransport();
        var total = 0L;
        await using var conn = new Npgsql.NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var queue in transport.Queues.Where(x => x.EndpointName.StartsWith("plettersonly")))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"select count(*) from pletters_pub_only_queues.{queue.QueueTable.Identifier.Name}";
            total += (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        total.ShouldBe(10);
    }
}
