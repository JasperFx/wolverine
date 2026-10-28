using System.Text;
using System.Text.Json;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Postgresql;
using Wolverine.Redis.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> keeps a handled row around to absorb a broker
/// redelivery. A stream entry from a producer that is not Wolverine carries no <c>wolverine-id</c> field, so the
/// id it is stored under has to come from the entry itself. Otherwise every delivery of it is a brand new
/// envelope, stored again and handled again.
///
/// <para>
/// A durable Redis listener acknowledges an entry as soon as it is in the inbox, so the same entry comes round
/// again when:
/// </para>
/// <list type="bullet">
/// <item>
/// The consumer group is moved back with <c>XGROUP SETID</c>, to replay the stream. Covered both while the first
/// copy is still Incoming -- its handler is held -- and after it was handled.
/// </item>
/// <item>
/// A consumer read the entry and died before acknowledging it, and the listener reclaims it from the consumer
/// group's pending entries list with <c>XAUTOCLAIM</c> (<c>EnableAutoClaim()</c>).
/// </item>
/// </list>
/// </summary>
public class redelivered_entry_without_wolverine_id_is_handled_once
{
    public enum EntryShape
    {
        // A payload field and the producer's own fields, but no "wolverine-id": someone else wrote it
        NoWolverineId,

        // The control: an entry carrying Wolverine's "wolverine-id" field, whose id was always stable
        WolverineId
    }

    public enum Redelivery
    {
        ReplayedWhileTheFirstCopyIsStillIncoming,
        ReplayedAfterTheFirstCopyWasHandled,
        ReclaimedFromAConsumerThatDiedBeforeAcknowledging
    }

    [Theory]
    [InlineData(EntryShape.NoWolverineId, Redelivery.ReplayedWhileTheFirstCopyIsStillIncoming)]
    [InlineData(EntryShape.NoWolverineId, Redelivery.ReplayedAfterTheFirstCopyWasHandled)]
    [InlineData(EntryShape.NoWolverineId, Redelivery.ReclaimedFromAConsumerThatDiedBeforeAcknowledging)]
    [InlineData(EntryShape.WolverineId, Redelivery.ReplayedWhileTheFirstCopyIsStillIncoming)]
    [InlineData(EntryShape.WolverineId, Redelivery.ReplayedAfterTheFirstCopyWasHandled)]
    [InlineData(EntryShape.WolverineId, Redelivery.ReclaimedFromAConsumerThatDiedBeforeAcknowledging)]
    public async Task a_redelivered_entry_is_handled_once(EntryShape shape, Redelivery redelivery)
    {
        var run = new TestRun(shape);

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "redis_foreign_redelivery");

                var listener = opts.ListenToRedisStream(run.StreamKey, run.ConsumerGroup)
                    .UseDurableInbox()
                    .StartFromBeginning()
                    .Sequential()

                    // The entries say nothing about their message type
                    .DefaultIncomingMessage<ForeignRedisOrderPlaced>();

                if (redelivery == Redelivery.ReclaimedFromAConsumerThatDiedBeforeAcknowledging)
                {
                    // The period stays above the listener's 1 second idle delay: an XAUTOCLAIM is due on every poll
                    // otherwise, and no new entry would ever be read with XREADGROUP
                    listener.EnableAutoClaim(period: 2.Seconds(), minIdle: 100.Milliseconds());
                }

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ForeignRedisOrderPlacedHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        await using var hostDisposal = (IAsyncDisposable)host;
        await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);
        run.Database = redis.GetDatabase();

        // The handled rows have to still be retained when the entries come round again
        host.GetRuntime().DurabilitySettings.KeepAfterMessageHandling.ShouldBeGreaterThan(1.Minutes());

        run.ListenerUri = host.GetRuntime().Options.Transports.GetOrCreate<RedisTransport>()
            .StreamEndpoint(run.StreamKey).Uri;
        var agent = host.GetRuntime().Endpoints.FindListeningAgent(run.ListenerUri);
        agent.ShouldNotBeNull();

        switch (redelivery)
        {
            case Redelivery.ReplayedWhileTheFirstCopyIsStillIncoming:
            {
                var firstCopy = ForeignRedisOrderPlacedHandler.HoldTheFirstHandlingOf(run.Id, 1);
                try
                {
                    await run.AddEntryAsync(1);
                    await firstCopy.Entered.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);

                    // The listener acknowledged entry 1 once it was in the inbox. Move the group back to the start
                    // of the stream while the first copy's handler is still running, so its row is still Incoming
                    await run.ReplayTheStreamFromTheBeginningAsync();
                    await run.AddEntryAsync(2);

                    // Entry 1 is read again ahead of the sentinel, so once the sentinel is in the inbox, entry 1
                    // has been stored again or refused
                    await run.WaitForInboxRowAsync(2);
                }
                finally
                {
                    firstCopy.Release();
                }

                // The listener is sequential: a stored second copy of entry 1 is handled ahead of the sentinel
                await ForeignRedisOrderPlacedHandler.WaitForAsync(run.Id, 2);
                await firstCopy.Completed.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);
                break;
            }

            case Redelivery.ReplayedAfterTheFirstCopyWasHandled:
                await run.AddEntryAsync(1);
                await ForeignRedisOrderPlacedHandler.WaitForAsync(run.Id, 1);

                // Let the handled mark land before the redelivery arrives
                await run.WaitForHandledRowsAsync(1);

                await run.ReplayTheStreamFromTheBeginningAsync();
                await run.AddEntryAsync(2);
                await ForeignRedisOrderPlacedHandler.WaitForAsync(run.Id, 2);
                break;

            case Redelivery.ReclaimedFromAConsumerThatDiedBeforeAcknowledging:
                await run.AddEntryAsync(1);
                await ForeignRedisOrderPlacedHandler.WaitForAsync(run.Id, 1);
                await run.WaitForHandledRowsAsync(1);

                // Another node reads entry 1 and dies before acknowledging it: the entry sits in the group's
                // pending entries list under that consumer until someone reclaims it
                await agent.StopAndDrainAsync();
                await run.ReplayTheStreamFromTheBeginningAsync();
                await run.ReadAndNeverAcknowledgeAsync("a-consumer-that-died");
                (await run.PendingEntriesAsync()).ShouldBe(1);

                // XAUTOCLAIM hands it to this listener, which acknowledges it once it is stored or refused
                await agent.StartAsync();
                await run.WaitForNoPendingEntriesAsync();
                await run.WaitForNoIncomingRowsAsync();
                break;
        }

        var history = ForeignRedisOrderPlacedHandler.Describe(run.Id);
        ForeignRedisOrderPlacedHandler.TimesHandled(run.Id, 1).ShouldBe(1, history);
        if (redelivery != Redelivery.ReclaimedFromAConsumerThatDiedBeforeAcknowledging)
        {
            ForeignRedisOrderPlacedHandler.TimesHandled(run.Id, 2).ShouldBe(1, history);
        }
    }

    private class TestRun
    {
        private readonly bool _withWolverineId;

        public TestRun(EntryShape shape)
        {
            Id = Guid.NewGuid().ToString("N");
            StreamKey = $"foreign-redelivery-{Id}";
            ConsumerGroup = $"foreign-redelivery-{Id}";
            _withWolverineId = shape == EntryShape.WolverineId;
        }

        public string Id { get; }
        public string StreamKey { get; }
        public string ConsumerGroup { get; }
        public IDatabase Database { get; set; } = null!;
        public Uri ListenerUri { get; set; } = null!;

        public async Task AddEntryAsync(int sequence)
        {
            // Plain XADD from some other producer: the JSON body in the "payload" field Wolverine's mapper reads,
            // next to fields of the producer's own
            var fields = new List<NameValueEntry>
            {
                new("payload", JsonSerializer.SerializeToUtf8Bytes(new ForeignRedisOrderPlaced(Id, sequence))),
                new("source", "/orders"),
                new("order-id", $"order-{sequence}")
            };

            if (_withWolverineId)
            {
                fields.Add(new NameValueEntry("wolverine-" + EnvelopeConstants.IdKey, Guid.NewGuid().ToString()));
            }

            await Database.StreamAddAsync(StreamKey, fields.ToArray());
        }

        public async Task ReplayTheStreamFromTheBeginningAsync()
        {
            await Database.StreamConsumerGroupSetPositionAsync(StreamKey, ConsumerGroup, StreamPosition.Beginning);
        }

        public async Task ReadAndNeverAcknowledgeAsync(string consumer)
        {
            var read = await Database.StreamReadGroupAsync(StreamKey, ConsumerGroup, consumer,
                StreamPosition.NewMessages, count: 1);
            read.Length.ShouldBe(1);
        }

        public async Task<long> PendingEntriesAsync()
        {
            return (await Database.StreamPendingAsync(StreamKey, ConsumerGroup)).PendingMessageCount;
        }

        public async Task WaitForNoPendingEntriesAsync()
        {
            await waitForAsync(async () => await PendingEntriesAsync() == 0, "the reclaimed entry to be acknowledged");
        }

        public Task WaitForInboxRowAsync(int sequence)
        {
            // The body column is the serialized envelope, which carries the entry's JSON verbatim
            return waitForRowsAsync("position(@marker in body) > 0",
                cmd => cmd.Parameters.AddWithValue("marker", Encoding.UTF8.GetBytes($"\"Sequence\":{sequence}")),
                count => count >= 1, $"the inbox row of sequence {sequence}");
        }

        public Task WaitForHandledRowsAsync(int count)
        {
            return waitForRowsAsync("status = 'Handled'", _ => { }, found => found >= count,
                $"{count} handled inbox rows");
        }

        public Task WaitForNoIncomingRowsAsync()
        {
            return waitForRowsAsync("status = 'Incoming'", _ => { }, found => found == 0,
                "every inbox row to be handled");
        }

        private async Task waitForRowsAsync(string predicate, Action<NpgsqlCommand> parameters,
            Func<long, bool> satisfied, string description)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();

            await waitForAsync(async () =>
            {
                await using var cmd = new NpgsqlCommand(
                    $"select count(*) from redis_foreign_redelivery.wolverine_incoming_envelopes where received_at = @uri and {predicate}",
                    conn);
                cmd.Parameters.AddWithValue("uri", ListenerUri.ToString());
                parameters(cmd);

                return satisfied((long)(await cmd.ExecuteScalarAsync())!);
            }, description);
        }

        private async Task waitForAsync(Func<Task<bool>> condition, string description)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (!await condition())
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"Never saw {description}. {ForeignRedisOrderPlacedHandler.Describe(Id)}");
                }

                await Task.Delay(100);
            }
        }
    }
}

public record ForeignRedisOrderPlaced(string RunId, int Sequence);

public static class ForeignRedisOrderPlacedHandler
{
    private static readonly object _lock = new();
    private static readonly List<(string RunId, int Sequence, Guid EnvelopeId, string? EntryId)> _handled = new();
    private static readonly Dictionary<(string RunId, int Sequence), HeldRedisHandling> _held = new();

    public static async Task Handle(ForeignRedisOrderPlaced message, Envelope envelope)
    {
        HeldRedisHandling? held;
        lock (_lock)
        {
            _held.TryGetValue((message.RunId, message.Sequence), out held);
        }

        var holding = held != null && held.TryEnter();
        if (holding)
        {
            await held!.Released;
        }

        envelope.Headers.TryGetValue(RedisEnvelopeMapper.RedisEntryIdHeader, out var entryId);
        lock (_lock)
        {
            _handled.Add((message.RunId, message.Sequence, envelope.Id, entryId));
        }

        if (holding)
        {
            held!.Complete();
        }
    }

    /// <summary>
    /// The first time this entry is handled, its handler waits until released, and its inbox row stays Incoming
    /// </summary>
    public static HeldRedisHandling HoldTheFirstHandlingOf(string runId, int sequence)
    {
        var held = new HeldRedisHandling();
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
                    .Select(x => $"sequence {x.Sequence} from entry {x.EntryId} as envelope {x.EnvelopeId}"));
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

public class HeldRedisHandling
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entries;

    public Task Entered => _entered.Task;
    public Task Released => _released.Task;

    // The held handling has been recorded, so the handled count of its entry is final once the redelivered copy
    // -- handled or refused -- is accounted for as well
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
