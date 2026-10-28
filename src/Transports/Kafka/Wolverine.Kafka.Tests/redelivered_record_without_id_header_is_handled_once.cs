using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> keeps a handled row around to absorb a broker
/// redelivery. A record from a producer that is not Wolverine carries no <c>id</c> header, so the id it is
/// stored under has to come from the record itself. Otherwise every read of it -- a consumer restart, a
/// rebalance, a rewind to the committed offset, a listener restarted after an inbox outage -- is a brand new
/// envelope, stored again and handled again.
///
/// <para>
/// Both orders a redelivery can arrive in are covered, with and without <c>EnableInboxPartitioning</c>:
/// </para>
/// <list type="bullet">
/// <item>
/// While the first copy is still Incoming. Its handler is held while the listener is paused without draining,
/// the way the inbox-outage handling pauses it, and restarted from an offset before the record.
/// </item>
/// <item>
/// After the first copy was handled, inside <see cref="DurabilitySettings.KeepAfterMessageHandling" />. The
/// listener is stopped, the consumer group is moved back to the start of the topic, and the listener is
/// started again.
/// </item>
/// </list>
/// <para>
/// In both, a sentinel record produced behind the redelivered one shows when the redelivery has been dealt
/// with: the listener is sequential, so the sentinel is stored and handled after it.
/// </para>
/// </summary>
public class redelivered_record_without_id_header_is_handled_once
{
    public enum RecordShape
    {
        // No "id" header, read by Wolverine's own KafkaEnvelopeMapper: the headers are someone else's
        NoIdHeader,

        // No "id" header, read by ReceiveRawJson<T>()'s JsonOnlyMapper, which reads no Wolverine metadata at all
        NoIdHeaderAsRawJson,

        // The control: a record carrying Wolverine's "id" header, whose id was always stable
        WolverineIdHeader
    }

    public enum Redelivery
    {
        WhileTheFirstCopyIsStillIncoming,
        AfterTheFirstCopyWasHandled
    }

    [Theory]
    [InlineData(RecordShape.NoIdHeader, false, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(RecordShape.NoIdHeader, false, Redelivery.AfterTheFirstCopyWasHandled)]
    [InlineData(RecordShape.NoIdHeader, true, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(RecordShape.NoIdHeader, true, Redelivery.AfterTheFirstCopyWasHandled)]
    [InlineData(RecordShape.NoIdHeaderAsRawJson, false, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(RecordShape.NoIdHeaderAsRawJson, false, Redelivery.AfterTheFirstCopyWasHandled)]
    [InlineData(RecordShape.NoIdHeaderAsRawJson, true, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(RecordShape.NoIdHeaderAsRawJson, true, Redelivery.AfterTheFirstCopyWasHandled)]
    [InlineData(RecordShape.WolverineIdHeader, false, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(RecordShape.WolverineIdHeader, false, Redelivery.AfterTheFirstCopyWasHandled)]
    [InlineData(RecordShape.WolverineIdHeader, true, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(RecordShape.WolverineIdHeader, true, Redelivery.AfterTheFirstCopyWasHandled)]
    public async Task a_redelivered_record_is_handled_once(RecordShape shape, bool partitionedInbox,
        Redelivery redelivery)
    {
        var run = new TestRun(shape, partitionedInbox);

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, run.Schema);
                opts.Durability.EnableInboxPartitioning = partitionedInbox;

                var listener = opts.ListenToKafkaTopic(run.TopicName)
                    .ExtendConsumerConfiguration(c =>
                    {
                        c.GroupId = run.GroupId;
                        c.AutoOffsetReset = AutoOffsetReset.Earliest;
                    })
                    .UseDurableInbox()
                    .Sequential();

                if (shape == RecordShape.NoIdHeaderAsRawJson)
                {
                    listener.ReceiveRawJson<ForeignOrderPlaced>();
                }
                else
                {
                    listener.DefaultIncomingMessage<ForeignOrderPlaced>();
                }

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ForeignOrderPlacedHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        // Disposed asynchronously so the listening agent, and the restart its pause below schedules, go with it
        await using var hostDisposal = (IAsyncDisposable)host;

        // The handled rows have to still be retained when the records come round again
        host.GetRuntime().DurabilitySettings.KeepAfterMessageHandling.ShouldBeGreaterThan(1.Minutes());

        var agent = host.GetRuntime().Endpoints.FindListeningAgent(new Uri($"kafka://topic/{run.TopicName}"));
        agent.ShouldNotBeNull();

        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString
        }).Build();

        if (redelivery == Redelivery.WhileTheFirstCopyIsStillIncoming)
        {
            var firstCopy = ForeignOrderPlacedHandler.HoldTheFirstHandlingOf(run.Id, 1);
            try
            {
                await run.ProduceAsync(producer, 1);
                await firstCopy.Entered.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);

                // A durable listener completes the record once it is in the inbox, so its offset is committed
                // already. Stop the consumer without waiting for the held handler, the way the inbox-outage
                // handling pauses a listener, and resume from an older offset, the way a consumer does that
                // restarts before its last offsets were committed
                await agent.PauseAsync(10.Minutes());
                await run.RewindConsumerGroupToTheBeginningAsync();
                await agent.StartAsync();

                await run.ProduceAsync(producer, 2);

                // The redelivered record 1 is ahead of the sentinel on the partition, so once the sentinel is in
                // the inbox, record 1 has been stored again or refused -- while its first copy is still Incoming
                await run.WaitForInboxRowAsync(2);
            }
            finally
            {
                firstCopy.Release();
            }

            // The redelivered copy is handled ahead of the sentinel or refused, and the held first copy has
            // finished: nothing is left in flight that could still handle record 1
            await ForeignOrderPlacedHandler.WaitForAsync(run.Id, 2);
            await firstCopy.Completed.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);
        }
        else
        {
            await run.ProduceAsync(producer, 1);
            await ForeignOrderPlacedHandler.WaitForAsync(run.Id, 1);

            // Let the handled mark land before the redelivery arrives
            await run.WaitForHandledRowsAsync(1);

            await agent.StopAndDrainAsync();
            await run.RewindConsumerGroupToTheBeginningAsync();
            await agent.StartAsync();

            await run.ProduceAsync(producer, 2);
            await ForeignOrderPlacedHandler.WaitForAsync(run.Id, 2);
        }

        var history = ForeignOrderPlacedHandler.Describe(run.Id);
        ForeignOrderPlacedHandler.TimesHandled(run.Id, 1).ShouldBe(1, history);
        ForeignOrderPlacedHandler.TimesHandled(run.Id, 2).ShouldBe(1, history);
    }

    private class TestRun
    {
        private readonly bool _withIdHeader;

        public TestRun(RecordShape shape, bool partitionedInbox)
        {
            Id = Guid.NewGuid().ToString("N");
            TopicName = $"foreign-redelivery-{Id}";
            GroupId = $"foreign-redelivery-{Id}";
            Schema = partitionedInbox ? "kafka_foreign_redelivery_partitioned" : "kafka_foreign_redelivery";
            _withIdHeader = shape == RecordShape.WolverineIdHeader;
        }

        public string Id { get; }
        public string TopicName { get; }
        public string GroupId { get; }
        public string Schema { get; }

        private string listenerUri => $"kafka://topic/{TopicName}";

        public async Task ProduceAsync(IProducer<string, byte[]> producer, int sequence)
        {
            // A binary-mode CloudEvent: its own id rides in ce_id, and there is no Wolverine "id" header
            var headers = new Headers
            {
                { "ce_specversion", Encoding.UTF8.GetBytes("1.0") },
                { "ce_id", Encoding.UTF8.GetBytes($"order-{sequence}") },
                { "ce_source", Encoding.UTF8.GetBytes("/orders") },
                { "ce_type", Encoding.UTF8.GetBytes("com.example.order.placed.v1") },
                { "content-type", Encoding.UTF8.GetBytes("application/json") }
            };

            if (_withIdHeader)
            {
                headers.Add(EnvelopeConstants.IdKey, Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
            }

            await producer.ProduceAsync(TopicName, new Message<string, byte[]>
            {
                Key = $"order-{sequence}",
                Value = JsonSerializer.SerializeToUtf8Bytes(new ForeignOrderPlaced(Id, sequence)),
                Headers = headers
            });
        }

        public async Task RewindConsumerGroupToTheBeginningAsync()
        {
            using var admin = buildAdminClient();
            var partitions = partitionsOf(admin);
            var beginning = partitions.Select(x => new TopicPartitionOffset(x, new Offset(0))).ToList();

            // The broker refuses to move the offsets of a group until the stopped consumer has left it
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (true)
            {
                try
                {
                    await admin.AlterConsumerGroupOffsetsAsync(
                        [new ConsumerGroupTopicPartitionOffsets(GroupId, beginning)]);
                    break;
                }
                catch (AlterConsumerGroupOffsetsException) when (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(500);
                }
            }

            await ConsumerGroupShouldBeAtAsync(0);
        }

        /// <summary>
        /// Without this the test could pass without anything having been redelivered at all: the restarted
        /// consumer has to start from no later than <paramref name="offset" />.
        /// </summary>
        public async Task ConsumerGroupShouldBeAtAsync(long offset)
        {
            using var admin = buildAdminClient();
            var committed = await admin.ListConsumerGroupOffsetsAsync(
                [new ConsumerGroupTopicPartitions(GroupId, partitionsOf(admin))]);

            // Offset.Unset (nothing committed yet) is negative, and also means "from the beginning" here
            committed.Single().Partitions.ShouldAllBe(x => x.Offset.Value <= offset);
        }

        public Task WaitForInboxRowAsync(int sequence)
        {
            // The body column is the serialized envelope, which carries the record's JSON verbatim
            return waitForRowsAsync("position(@marker in body) > 0",
                cmd => cmd.Parameters.AddWithValue("marker", Encoding.UTF8.GetBytes($"\"Sequence\":{sequence}")), 1,
                $"the inbox row of sequence {sequence}");
        }

        public Task WaitForHandledRowsAsync(int count)
        {
            return waitForRowsAsync("status = 'Handled'", _ => { }, count, $"{count} handled inbox rows");
        }

        private async Task waitForRowsAsync(string predicate, Action<NpgsqlCommand> parameters, int count,
            string description)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();

            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (true)
            {
                await using var cmd = new NpgsqlCommand(
                    $"select count(*) from {Schema}.wolverine_incoming_envelopes where received_at = @uri and {predicate}",
                    conn);
                cmd.Parameters.AddWithValue("uri", listenerUri);
                parameters(cmd);

                var found = (long)(await cmd.ExecuteScalarAsync())!;
                if (found >= count) return;

                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException($"Never found {description}. {ForeignOrderPlacedHandler.Describe(Id)}");
                }

                await Task.Delay(100);
            }
        }

        private static IAdminClient buildAdminClient()
        {
            return new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = KafkaContainerFixture.ConnectionString
            }).Build();
        }

        private List<TopicPartition> partitionsOf(IAdminClient admin)
        {
            return admin.GetMetadata(TopicName, 10.Seconds()).Topics.Single().Partitions
                .Select(x => new TopicPartition(TopicName, new Partition(x.PartitionId)))
                .ToList();
        }
    }
}

public record ForeignOrderPlaced(string RunId, int Sequence);

public static class ForeignOrderPlacedHandler
{
    private static readonly object _lock = new();
    private static readonly List<(string RunId, int Sequence, Guid EnvelopeId, long Offset)> _handled = new();
    private static readonly Dictionary<(string RunId, int Sequence), HeldHandling> _held = new();

    public static async Task Handle(ForeignOrderPlaced message, Envelope envelope)
    {
        HeldHandling? held;
        lock (_lock)
        {
            _held.TryGetValue((message.RunId, message.Sequence), out held);
        }

        var holding = held != null && held.TryEnter();
        if (holding)
        {
            await held!.Released;
        }

        lock (_lock)
        {
            _handled.Add((message.RunId, message.Sequence, envelope.Id, envelope.Offset));
        }

        if (holding)
        {
            held!.Complete();
        }
    }

    /// <summary>
    /// The first time this record is handled, its handler waits until released, and its inbox row stays Incoming
    /// </summary>
    public static HeldHandling HoldTheFirstHandlingOf(string runId, int sequence)
    {
        var held = new HeldHandling();
        lock (_lock)
        {
            _held[(runId, sequence)] = held;
        }

        return held;
    }

    public static int TimesHandled(string runId, int sequence)
    {
        lock (_lock)
        {
            return _handled.Count(x => x.RunId == runId && x.Sequence == sequence);
        }
    }

    public static string Describe(string runId)
    {
        lock (_lock)
        {
            return "Handled: " + string.Join(", ",
                _handled.Where(x => x.RunId == runId)
                    .Select(x => $"sequence {x.Sequence} at offset {x.Offset} as envelope {x.EnvelopeId}"));
        }
    }

    public static async Task WaitForAsync(string runId, int sequence)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (TimesHandled(runId, sequence) == 0)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Sequence {sequence} of run {runId} was never handled. {Describe(runId)}");
            }

            await Task.Delay(100);
        }
    }
}

public class HeldHandling
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entries;

    public Task Entered => _entered.Task;
    public Task Released => _released.Task;

    // The held handling has been recorded, so the handled count of its record is final once the redelivered
    // copy -- handled or refused -- is accounted for as well
    public Task Completed => _completed.Task;

    public void Complete()
    {
        _completed.TrySetResult();
    }

    public bool TryEnter()
    {
        if (Interlocked.Increment(ref _entries) != 1) return false;

        _entered.TrySetResult();
        return true;
    }

    public void Release()
    {
        _released.TrySetResult();
    }
}
