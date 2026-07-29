using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
///     GH-3613. Both sweeps over the <c>{stream}:scheduled</c> sorted set used to respond to a payload they
///     could not deserialize by REMOVING it: <c>DeleteExpiredAsync</c> deleted it as "corrupted", and
///     <c>MoveScheduledToReadyStreamAsync</c> claimed the entry before attempting to read it, so the
///     entry was already gone when the read threw. Either way a scheduled retry vanished without ever being
///     redelivered and without reaching <c>MoveToErrorQueue()</c> / the dead letter queue.
/// </summary>
[Collection("ScheduledEntryRetention3613")]
public class scheduled_entries_are_never_silently_dropped_3613 : IAsyncLifetime
{
    private IHost _host = null!;
    private RedisStreamListener _listener = null!;
    private IDatabase _database = null!;
    private string _scheduledKey = null!;
    private string _streamKey = null!;

    public async ValueTask InitializeAsync()
    {
        _streamKey = $"retention-3613-{Guid.NewGuid():N}";

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ScheduledEntryRetention3613";
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();
                opts.ListenToRedisStream(_streamKey, "retention-3613-group").StartFromBeginning();
            }).StartAsync();

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(_streamKey);

        _database = transport.GetDatabase(database: 0);
        _scheduledKey = endpoint.ScheduledMessagesKey;

        // Drive the sweeps directly rather than waiting on the polling loop.
        _listener = new RedisStreamListener(transport, endpoint, runtime, Substitute.For<IReceiver>());

        await _database.KeyDeleteAsync(_scheduledKey);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.KeyDeleteAsync(_scheduledKey);
        await _listener.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }

    // Not a valid serialized Envelope, so EnvelopeSerializer.Deserialize throws on it. Stands in for the
    // GH-3613 payload, whose 'deliver-by' value made the read blow up.
    private static readonly RedisValue UnreadablePayload = new byte[] { 0xFF, 0x00, 0x13, 0x37, 0x42 };

    // Pre-serialized Data so the envelope round-trips without needing a registered message type.
    private static Envelope readableEnvelope()
    {
        return new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "retention-message",
            ContentType = EnvelopeConstants.JsonContentType,
            Data = [1, 2, 3]
        };
    }

    [Fact]
    public async Task delete_expired_leaves_an_entry_it_cannot_deserialize_in_place()
    {
        // Due in the past, so the sweep definitely inspects it.
        await _database.SortedSetAddAsync(_scheduledKey, UnreadablePayload,
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());

        await _listener.DeleteExpiredAsync(CancellationToken.None);

        (await _database.SortedSetLengthAsync(_scheduledKey)).ShouldBe(1,
            "an unreadable scheduled entry must not be treated as expired and deleted");
    }

    [Fact]
    public async Task delete_expired_still_evicts_a_genuinely_expired_message()
    {
        var envelope = readableEnvelope();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(-1);

        await _database.SortedSetAddAsync(_scheduledKey, EnvelopeSerializer.Serialize(envelope),
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());

        await _listener.DeleteExpiredAsync(CancellationToken.None);

        (await _database.SortedSetLengthAsync(_scheduledKey)).ShouldBe(0,
            "a readable message past its DeliverBy is still supposed to be evicted");
    }

    [Fact]
    public async Task delete_expired_leaves_a_message_that_is_not_yet_expired()
    {
        var envelope = readableEnvelope();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(10);

        await _database.SortedSetAddAsync(_scheduledKey, EnvelopeSerializer.Serialize(envelope),
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());

        await _listener.DeleteExpiredAsync(CancellationToken.None);

        (await _database.SortedSetLengthAsync(_scheduledKey)).ShouldBe(1);
    }

    [Fact]
    public async Task moving_due_entries_leaves_one_it_cannot_deserialize_in_place()
    {
        await _database.SortedSetAddAsync(_scheduledKey, UnreadablePayload,
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());

        var moved = await _listener.MoveScheduledToReadyStreamAsync(CancellationToken.None);

        moved.ShouldBe(0);
        (await _database.SortedSetLengthAsync(_scheduledKey)).ShouldBe(1,
            "the entry must not be claimed out of the sorted set before it can be read");
    }

    [Fact]
    public async Task moving_due_entries_still_promotes_a_readable_message_to_the_stream()
    {
        var envelope = readableEnvelope();
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1);

        await _database.SortedSetAddAsync(_scheduledKey, EnvelopeSerializer.Serialize(envelope),
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());

        var moved = await _listener.MoveScheduledToReadyStreamAsync(CancellationToken.None);

        moved.ShouldBe(1);
        (await _database.SortedSetLengthAsync(_scheduledKey)).ShouldBe(0);
        (await _database.StreamLengthAsync(_streamKey)).ShouldBeGreaterThan(0);
    }
}

[CollectionDefinition("ScheduledEntryRetention3613", DisableParallelization = true)]
public class ScheduledEntryRetention3613Collection;
