using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

/// <summary>
/// Unit tests for RedisSenderProtocol verifying native scheduling behavior.
/// These tests verify that:
/// 1. Immediate messages are sent directly to Redis streams
/// 2. Scheduled messages are stored in Redis sorted sets for later execution
/// 3. The scheduling happens at the transport level (no in-memory job scheduling)
/// </summary>
[Collection("RedisSenderProtocolTests")]
public class RedisSenderProtocolTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;
    private IWolverineRuntime? _runtime;
    private RedisTransport? _transport;
    private IDatabase? _database;
    private string _streamKey = "";

    public RedisSenderProtocolTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _streamKey = $"sender-protocol-test-{Guid.NewGuid():N}";
        
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "SenderProtocolTestService";
                opts.UseRedisTransport("localhost:6379").AutoProvision();
            }).StartAsync();

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        _transport = _runtime.Options.Transports.GetOrCreate<RedisTransport>();
        _database = _transport.GetDatabase(database: 0);
        
        // Clean up any existing data
        await _database.KeyDeleteAsync(_streamKey);
        await _database.KeyDeleteAsync($"{_streamKey}:scheduled");
    }

    public async Task DisposeAsync()
    {
        if (_database != null)
        {
            await _database.KeyDeleteAsync(_streamKey);
            await _database.KeyDeleteAsync($"{_streamKey}:scheduled");
        }
        
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public void implements_native_scheduling_interface()
    {
        // Verify the protocol implements ISenderProtocolWithNativeScheduling
        typeof(ISenderProtocolWithNativeScheduling).IsAssignableFrom(typeof(RedisSenderProtocol))
            .ShouldBeTrue("RedisSenderProtocol should implement ISenderProtocolWithNativeScheduling");
        
        _output.WriteLine("✓ RedisSenderProtocol implements ISenderProtocolWithNativeScheduling");
        _output.WriteLine("  This marker interface tells Wolverine that native scheduling is supported");
    }

    [Fact]
    public async Task sends_immediate_message_to_redis_stream()
    {
        // Arrange
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        var callback = Substitute.For<ISenderCallback>();
        
        var message = new SenderProtocolTestMessage("test-immediate");
        var envelope = CreateEnvelope(message);
        // No ScheduledTime = immediate message
        
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { envelope });

        // Act
        await protocol.SendBatchAsync(callback, batch);

        // Assert
        var streamLength = await _database!.StreamLengthAsync(_streamKey);
        streamLength.ShouldBe(1, "Immediate message should be in stream");
        
        var scheduledCount = await _database.SortedSetLengthAsync($"{_streamKey}:scheduled");
        scheduledCount.ShouldBe(0, "Immediate message should NOT be in scheduled set");
        
        await callback.Received(1).MarkSuccessfulAsync(batch);
        
        _output.WriteLine("✓ Immediate message sent directly to Redis stream");
    }

    [Fact]
    public async Task sends_scheduled_message_to_sorted_set()
    {
        // Arrange
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        var callback = Substitute.For<ISenderCallback>();
        
        var message = new SenderProtocolTestMessage("test-scheduled");
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var envelope = CreateEnvelope(message);
        envelope.ScheduledTime = scheduledTime;
        
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { envelope });

        // Act
        await protocol.SendBatchAsync(callback, batch);

        // Assert
        var streamLength = await _database!.StreamLengthAsync(_streamKey);
        streamLength.ShouldBe(0, "Scheduled message should NOT be in stream");
        
        var scheduledCount = await _database.SortedSetLengthAsync($"{_streamKey}:scheduled");
        scheduledCount.ShouldBe(1, "Scheduled message should be in scheduled set");
        
        // Verify the score matches the scheduled time
        var entries = await _database.SortedSetRangeByScoreWithScoresAsync($"{_streamKey}:scheduled");
        entries.Length.ShouldBe(1);
        var expectedScore = scheduledTime.ToUnixTimeMilliseconds();
        Math.Abs(entries[0].Score - expectedScore).ShouldBeLessThan(1000, "Score should match scheduled time");
        
        await callback.Received(1).MarkSuccessfulAsync(batch);
        
        _output.WriteLine("✓ Scheduled message stored in Redis sorted set with correct score");
        _output.WriteLine($"  Scheduled for: {scheduledTime}");
        _output.WriteLine($"  Score: {entries[0].Score} (expected: {expectedScore})");
    }

    [Fact]
    public async Task sends_mixed_batch_to_appropriate_destinations()
    {
        // Arrange
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        var callback = Substitute.For<ISenderCallback>();
        
        var immediateMessage = new SenderProtocolTestMessage("immediate");
        var scheduledMessage = new SenderProtocolTestMessage("scheduled");
        
        var immediateEnvelope = CreateEnvelope(immediateMessage);
        var scheduledEnvelope = CreateEnvelope(scheduledMessage);
        scheduledEnvelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
        
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { immediateEnvelope, scheduledEnvelope });

        // Act
        await protocol.SendBatchAsync(callback, batch);

        // Assert
        var streamLength = await _database!.StreamLengthAsync(_streamKey);
        streamLength.ShouldBe(1, "Only immediate message should be in stream");
        
        var scheduledCount = await _database.SortedSetLengthAsync($"{_streamKey}:scheduled");
        scheduledCount.ShouldBe(1, "Only scheduled message should be in scheduled set");
        
        await callback.Received(1).MarkSuccessfulAsync(batch);
        
        _output.WriteLine("✓ Mixed batch: immediate → stream, scheduled → sorted set");
    }

    [Fact]
    public async Task treats_past_scheduled_time_as_immediate()
    {
        // Arrange
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        var callback = Substitute.For<ISenderCallback>();
        
        var message = new SenderProtocolTestMessage("past-scheduled");
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var envelope = CreateEnvelope(message);
        envelope.ScheduledTime = pastTime;
        
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { envelope });

        // Act
        await protocol.SendBatchAsync(callback, batch);

        // Assert - past scheduled time should go to stream (immediate)
        var streamLength = await _database!.StreamLengthAsync(_streamKey);
        streamLength.ShouldBe(1, "Past-scheduled message should be in stream (treated as immediate)");
        
        var scheduledCount = await _database.SortedSetLengthAsync($"{_streamKey}:scheduled");
        scheduledCount.ShouldBe(0, "Past-scheduled message should NOT be in scheduled set");
        
        await callback.Received(1).MarkSuccessfulAsync(batch);
        
        _output.WriteLine("✓ Past scheduled time treated as immediate - sent to stream");
    }

    [Fact]
    public async Task sends_multiple_immediate_messages_in_batch()
    {
        // Arrange
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        var callback = Substitute.For<ISenderCallback>();
        
        var envelopes = Enumerable.Range(0, 5)
            .Select(i => CreateEnvelope(new SenderProtocolTestMessage($"msg-{i}")))
            .ToArray();
        
        var batch = new OutgoingMessageBatch(endpoint.Uri, envelopes);

        // Act
        await protocol.SendBatchAsync(callback, batch);

        // Assert
        var streamLength = await _database!.StreamLengthAsync(_streamKey);
        streamLength.ShouldBe(5, "All immediate messages should be in stream");
        
        await callback.Received(1).MarkSuccessfulAsync(batch);
        
        _output.WriteLine("✓ Multiple immediate messages sent to stream");
    }

    [Fact]
    public async Task sends_multiple_scheduled_messages_in_batch()
    {
        // Arrange
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        var callback = Substitute.For<ISenderCallback>();
        
        var baseTime = DateTimeOffset.UtcNow;
        var envelopes = Enumerable.Range(0, 5).Select(i =>
        {
            var env = CreateEnvelope(new SenderProtocolTestMessage($"scheduled-{i}"));
            env.ScheduledTime = baseTime.AddMinutes(i + 1);
            return env;
        }).ToArray();
        
        var batch = new OutgoingMessageBatch(endpoint.Uri, envelopes);

        // Act
        await protocol.SendBatchAsync(callback, batch);

        // Assert
        var scheduledCount = await _database!.SortedSetLengthAsync($"{_streamKey}:scheduled");
        scheduledCount.ShouldBe(5, "All scheduled messages should be in sorted set");
        
        var streamLength = await _database.StreamLengthAsync(_streamKey);
        streamLength.ShouldBe(0, "No messages should be in stream");
        
        await callback.Received(1).MarkSuccessfulAsync(batch);
        
        _output.WriteLine("✓ Multiple scheduled messages stored in sorted set");
    }

    [Fact]
    public async Task uses_correct_score_for_scheduled_time()
    {
        // Arrange
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        var callback = Substitute.For<ISenderCallback>();
        
        var scheduledTime = new DateTimeOffset(2030, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var expectedScore = scheduledTime.ToUnixTimeMilliseconds();
        
        var envelope = CreateEnvelope(new SenderProtocolTestMessage("score-test"));
        envelope.ScheduledTime = scheduledTime;
        
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { envelope });

        // Act
        await protocol.SendBatchAsync(callback, batch);

        // Assert
        var entries = await _database!.SortedSetRangeByScoreWithScoresAsync($"{_streamKey}:scheduled");
        entries.Length.ShouldBe(1);
        entries[0].Score.ShouldBe(expectedScore, "Score should exactly match scheduled time in milliseconds");
        
        _output.WriteLine("✓ Correct score used for scheduled time");
        _output.WriteLine($"  Expected: {expectedScore}, Actual: {entries[0].Score}");
    }

    [Fact]
    public async Task scheduled_message_can_be_deserialized()
    {
        // Arrange - verify the serialized message can be deserialized back
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        var callback = Substitute.For<ISenderCallback>();
        
        var originalId = Guid.NewGuid();
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var envelope = CreateEnvelope(new SenderProtocolTestMessage("deserialize-test"));
        envelope.Id = originalId;
        envelope.ScheduledTime = scheduledTime;
        
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { envelope });

        // Act
        await protocol.SendBatchAsync(callback, batch);

        // Assert - read back and deserialize
        var entries = await _database!.SortedSetRangeByScoreAsync($"{_streamKey}:scheduled");
        entries.Length.ShouldBe(1);
        
        var deserializedEnvelope = EnvelopeSerializer.Deserialize((byte[])entries[0]!);
        deserializedEnvelope.Id.ShouldBe(originalId);
        deserializedEnvelope.ScheduledTime.ShouldNotBeNull();
        Math.Abs((deserializedEnvelope.ScheduledTime!.Value - scheduledTime).TotalSeconds).ShouldBeLessThan(1);
        
        _output.WriteLine("✓ Scheduled message can be deserialized correctly");
        _output.WriteLine($"  Original ID: {originalId}");
        _output.WriteLine($"  Deserialized ID: {deserializedEnvelope.Id}");
    }

    [Fact]
    public void dispose_does_not_throw()
    {
        // Arrange
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        
        // Act & Assert - Dispose should not throw
        Should.NotThrow(() => protocol.Dispose());
        
        _output.WriteLine("✓ Dispose completes without throwing");
    }

    [Fact]
    public async Task marks_callback_successful_after_send()
    {
        // Arrange
        var endpoint = _transport!.StreamEndpoint(_streamKey);
        endpoint.EnvelopeMapper = new RedisEnvelopeMapper(endpoint);
        
        var protocol = new RedisSenderProtocol(_transport, endpoint);
        var callback = Substitute.For<ISenderCallback>();
        
        var envelope = CreateEnvelope(new SenderProtocolTestMessage("callback-test"));
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { envelope });

        // Act
        await protocol.SendBatchAsync(callback, batch);

        // Assert
        await callback.Received(1).MarkSuccessfulAsync(batch);
        await callback.DidNotReceive().MarkProcessingFailureAsync(Arg.Any<OutgoingMessageBatch>(), Arg.Any<Exception>());
        
        _output.WriteLine("✓ Callback marked successful after successful send");
    }

    private Envelope CreateEnvelope(SenderProtocolTestMessage message)
    {
        var serializer = _runtime!.Options.DefaultSerializer;
        return new Envelope(message)
        {
            Id = Guid.NewGuid(),
            MessageType = typeof(SenderProtocolTestMessage).ToMessageTypeName(),
            Data = serializer.WriteMessage(message),
            ContentType = serializer.ContentType
        };
    }
}

public record SenderProtocolTestMessage(string Id);
