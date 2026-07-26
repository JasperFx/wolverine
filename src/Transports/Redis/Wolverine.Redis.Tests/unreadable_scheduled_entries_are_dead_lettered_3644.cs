using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
///     GH-3644, the residual of GH-3613. That fix stopped the scheduled sweeps deleting entries they could not
///     deserialize, because doing so was silently destroying scheduled retries. The cost was that a genuinely
///     unreadable payload then stayed in <c>{stream}:scheduled</c> forever and re-warned on every sweep.
///
///     <para>Now the sweep counts consecutive read failures per entry and, once
///     <see cref="RedisTransport.MaxScheduledReadAttempts" /> is reached, moves the raw bytes to the dead letter
///     stream. The entry is never simply dropped, and never removed from the sorted set until it is safely in
///     the dead letter stream.</para>
/// </summary>
[Collection("UnreadableScheduled3644")]
public class unreadable_scheduled_entries_are_dead_lettered_3644 : IAsyncLifetime
{
    private IHost _host = null!;
    private RedisStreamListener _listener = null!;
    private RedisTransport _transport = null!;
    private IDatabase _database = null!;
    private RedisStreamEndpoint _endpoint = null!;

    // Not a valid serialized Envelope, so EnvelopeSerializer.Deserialize throws on it every time.
    private static readonly RedisValue UnreadablePayload = new byte[] { 0xFF, 0x00, 0x13, 0x37, 0x42 };

    public async Task InitializeAsync()
    {
        var streamKey = $"unreadable-3644-{Guid.NewGuid():N}";

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Unreadable3644";
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();
                opts.ListenToRedisStream(streamKey, "unreadable-3644-group")
                    .StartFromBeginning()
                    .EnableNativeDeadLetterQueue();
            }).StartAsync();

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        _transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        _endpoint = _transport.StreamEndpoint(streamKey);
        _database = _transport.GetDatabase(database: 0);

        _listener = new RedisStreamListener(_transport, _endpoint, runtime, Substitute.For<IReceiver>());

        await resetAsync();
    }

    private async Task resetAsync()
    {
        await _database.KeyDeleteAsync(_endpoint.ScheduledMessagesKey);
        await _database.KeyDeleteAsync(_endpoint.UnreadableScheduledMessagesKey);
        await _database.KeyDeleteAsync(_endpoint.DeadLetterQueueKey);
    }

    public async Task DisposeAsync()
    {
        await resetAsync();
        await _listener.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task planAsync() => _database.SortedSetAddAsync(_endpoint.ScheduledMessagesKey, UnreadablePayload,
        DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());

    private Task<long> scheduledCountAsync() => _database.SortedSetLengthAsync(_endpoint.ScheduledMessagesKey);

    private Task<long> deadLetterCountAsync() => _database.StreamLengthAsync(_endpoint.DeadLetterQueueKey);

    [Fact]
    public async Task an_unreadable_entry_survives_the_attempts_before_the_limit()
    {
        _transport.MaxScheduledReadAttempts = 3;
        await planAsync();

        await _listener.DeleteExpiredAsync(CancellationToken.None);
        (await scheduledCountAsync()).ShouldBe(1, "still under the limit");

        await _listener.DeleteExpiredAsync(CancellationToken.None);
        (await scheduledCountAsync()).ShouldBe(1, "still under the limit");

        (await deadLetterCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task an_unreadable_entry_is_dead_lettered_once_the_limit_is_reached()
    {
        _transport.MaxScheduledReadAttempts = 3;
        await planAsync();

        for (var i = 0; i < 3; i++)
        {
            await _listener.DeleteExpiredAsync(CancellationToken.None);
        }

        (await scheduledCountAsync()).ShouldBe(0, "the entry should have left the scheduled set");
        (await deadLetterCountAsync()).ShouldBe(1, "...by way of the dead letter queue, not by being dropped");
    }

    [Fact]
    public async Task the_dead_lettered_entry_carries_the_original_bytes_and_diagnostics()
    {
        _transport.MaxScheduledReadAttempts = 1;
        await planAsync();

        await _listener.DeleteExpiredAsync(CancellationToken.None);

        var entries = await _database.StreamRangeAsync(_endpoint.DeadLetterQueueKey);
        entries.Length.ShouldBe(1);

        var fields = entries[0].Values.ToDictionary(x => x.Name.ToString(), x => x.Value);

        // The raw payload has to survive verbatim -- it is the only way an operator can recover it.
        ((byte[])fields["envelope"]!).ShouldBe((byte[])UnreadablePayload!);
        fields["unreadable-scheduled-message"].ToString().ShouldBe("true");
        fields[DeadLetterQueueConstants.ExceptionTypeHeader].ToString().ShouldNotBeNullOrEmpty();
        fields[DeadLetterQueueConstants.OriginalDestinationHeader].ToString().ShouldBe(_endpoint.Uri.ToString());
    }

    [Fact]
    public async Task the_failure_counter_is_cleaned_up_after_dead_lettering()
    {
        _transport.MaxScheduledReadAttempts = 1;
        await planAsync();

        await _listener.DeleteExpiredAsync(CancellationToken.None);

        (await _database.HashLengthAsync(_endpoint.UnreadableScheduledMessagesKey)).ShouldBe(0);
    }

    [Fact]
    public async Task setting_the_limit_to_zero_keeps_the_GH_3613_behaviour()
    {
        // The escape hatch: never dead-letter, keep the entry indefinitely.
        _transport.MaxScheduledReadAttempts = 0;
        await planAsync();

        for (var i = 0; i < 5; i++)
        {
            await _listener.DeleteExpiredAsync(CancellationToken.None);
        }

        (await scheduledCountAsync()).ShouldBe(1);
        (await deadLetterCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task an_endpoint_without_a_native_dead_letter_queue_keeps_the_entry()
    {
        // Nowhere safe to put it, so it stays put rather than being destroyed.
        _transport.MaxScheduledReadAttempts = 1;
        _endpoint.NativeDeadLetterQueueEnabled = false;
        try
        {
            await planAsync();

            await _listener.DeleteExpiredAsync(CancellationToken.None);

            (await scheduledCountAsync()).ShouldBe(1);
        }
        finally
        {
            _endpoint.NativeDeadLetterQueueEnabled = true;
        }
    }

    [Fact]
    public async Task a_readable_expired_message_is_still_evicted_normally()
    {
        // Guard against the counting path swallowing the sweep's actual job.
        _transport.MaxScheduledReadAttempts = 3;

        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "retention-message",
            ContentType = EnvelopeConstants.JsonContentType,
            Data = [1, 2, 3],
            DeliverBy = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        await _database.SortedSetAddAsync(_endpoint.ScheduledMessagesKey,
            Runtime.Serialization.EnvelopeSerializer.Serialize(envelope),
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());

        await _listener.DeleteExpiredAsync(CancellationToken.None);

        (await scheduledCountAsync()).ShouldBe(0);
        (await deadLetterCountAsync()).ShouldBe(0, "an expired message is evicted, not dead-lettered");
    }
}

[CollectionDefinition("UnreadableScheduled3644", DisableParallelization = true)]
public class UnreadableScheduled3644Collection;
