using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

[Collection("DatabaseBackedEndpointTests")]
public class DatabaseBackedEndpointTests
{
    private readonly ITestOutputHelper _output;

    public DatabaseBackedEndpointTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task redis_endpoint_implements_idatabase_backed_endpoint()
    {
        var streamKey = $"dbe-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DatabaseBackedEndpointTest";
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                opts.PublishMessage<TestMessage>().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "dbe-test-group").StartFromBeginning();
            }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);

        // Verify it implements IDatabaseBackedEndpoint
        endpoint.ShouldBeAssignableTo<IDatabaseBackedEndpoint>();

        _output.WriteLine("✓ RedisStreamEndpoint implements IDatabaseBackedEndpoint");
    }

    [Fact]
    public async Task schedule_retry_should_add_message_to_scheduled_set()
    {
        var streamKey = $"dbe-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DatabaseBackedEndpointTest";
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                opts.PublishMessage<TestMessage>().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "dbe-test-group").StartFromBeginning();
            }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey) as IDatabaseBackedEndpoint;
        var database = transport.GetDatabase(database: 0);
        var scheduledKey = transport.StreamEndpoint(streamKey).ScheduledMessagesKey;

        // Clear the scheduled set
        await database.KeyDeleteAsync(scheduledKey);

        // Create an envelope to schedule for retry
        var message = new TestMessage("test-retry");
        var envelope = new Envelope(message)
        {
            Id = Guid.NewGuid(),
            ScheduledTime = DateTimeOffset.UtcNow.AddSeconds(30),
            MessageType = typeof(TestMessage).ToMessageTypeName()
        };
        
        // Ensure the envelope has serialized data (simulating a real envelope that was received and failed)
        var writer = runtime.Options.DefaultSerializer;
        envelope.Data = writer.WriteMessage(message);
        envelope.ContentType = writer.ContentType;

        // Schedule the retry
        await endpoint!.ScheduleRetryAsync(envelope, CancellationToken.None);

        // Wait a moment for Redis to persist
        await Task.Delay(200);

        // Verify the message is in the scheduled set
        var scheduledCount = await database.SortedSetLengthAsync(scheduledKey);
        scheduledCount.ShouldBe(1, "Message should be in scheduled set");

        // Verify the score is correct
        var entries = await database.SortedSetRangeByScoreWithScoresAsync(scheduledKey);
        entries.Length.ShouldBe(1);
        
        var expectedScore = envelope.ScheduledTime!.Value.ToUnixTimeMilliseconds();
        entries[0].Score.ShouldBe(expectedScore, 1000, "Score should be close to expected Unix timestamp");

        // Verify we can deserialize the envelope
        var storedEnvelope = EnvelopeSerializer.Deserialize((byte[])entries[0].Element!);
        storedEnvelope.Id.ShouldBe(envelope.Id);
        storedEnvelope.MessageType.ShouldBe(envelope.MessageType);

        _output.WriteLine($"✓ Scheduled retry added to Redis sorted set");
        _output.WriteLine($"  Expected score: {expectedScore}");
        _output.WriteLine($"  Actual score: {entries[0].Score}");
        _output.WriteLine($"  Envelope ID: {storedEnvelope.Id}");
    }

    [Fact]
    public async Task schedule_retry_without_scheduled_time_uses_default_delay()
    {
        var streamKey = $"dbe-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DatabaseBackedEndpointTest";
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                opts.PublishMessage<TestMessage>().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "dbe-test-group").StartFromBeginning();
            }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey) as IDatabaseBackedEndpoint;
        var database = transport.GetDatabase(database: 0);
        var scheduledKey = transport.StreamEndpoint(streamKey).ScheduledMessagesKey;

        await database.KeyDeleteAsync(scheduledKey);

        var message = new TestMessage("test-default-delay");
        var envelope = new Envelope(message)
        {
            Id = Guid.NewGuid(),
            ScheduledTime = null, // No scheduled time - should use default
            MessageType = typeof(TestMessage).ToMessageTypeName()
        };
        
        // Ensure the envelope has serialized data
        var writer = runtime.Options.DefaultSerializer;
        envelope.Data = writer.WriteMessage(message);
        envelope.ContentType = writer.ContentType;

        var beforeSchedule = DateTimeOffset.UtcNow;
        await endpoint!.ScheduleRetryAsync(envelope, CancellationToken.None);
        var afterSchedule = DateTimeOffset.UtcNow;

        await Task.Delay(200);

        var entries = await database.SortedSetRangeByScoreWithScoresAsync(scheduledKey);
        entries.Length.ShouldBe(1);

        // Score should be roughly 5 seconds from now (default retry delay)
        var expectedMinScore = beforeSchedule.AddSeconds(4).ToUnixTimeMilliseconds();
        var expectedMaxScore = afterSchedule.AddSeconds(6).ToUnixTimeMilliseconds();
        
        entries[0].Score.ShouldBeGreaterThanOrEqualTo(expectedMinScore);
        entries[0].Score.ShouldBeLessThanOrEqualTo(expectedMaxScore);

        _output.WriteLine($"✓ Default 5 second retry delay applied");
        _output.WriteLine($"  Score: {entries[0].Score}");
        _output.WriteLine($"  Expected range: {expectedMinScore} - {expectedMaxScore}");
    }

    [Fact]
    public async Task scheduled_retry_should_be_picked_up_by_polling()
    {
        var streamKey = $"dbe-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DatabaseBackedEndpointTest";
                
                // Fast polling for this test
                opts.Durability.ScheduledJobFirstExecution = 50.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                opts.PublishMessage<TestMessage>().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "dbe-test-group").StartFromBeginning();
            }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey) as IDatabaseBackedEndpoint;
        var database = transport.GetDatabase(database: 0);
        var scheduledKey = transport.StreamEndpoint(streamKey).ScheduledMessagesKey;

        await database.KeyDeleteAsync(scheduledKey);

        // Schedule a retry for 2 seconds from now
        var message = new TestMessage("test-polling");
        var envelope = new Envelope(message)
        {
            Id = Guid.NewGuid(),
            ScheduledTime = DateTimeOffset.UtcNow.AddSeconds(2),
            MessageType = typeof(TestMessage).ToMessageTypeName()
        };
        
        // Ensure the envelope has serialized data
        var writer = runtime.Options.DefaultSerializer;
        envelope.Data = writer.WriteMessage(message);
        envelope.ContentType = writer.ContentType;

        await endpoint!.ScheduleRetryAsync(envelope, CancellationToken.None);
        await Task.Delay(200);

        // Verify it's in scheduled set
        var initialCount = await database.SortedSetLengthAsync(scheduledKey);
        initialCount.ShouldBe(1);

        // Wait for polling to move it to the stream
        await Task.Delay(3000);

        // Verify it's been removed from scheduled set
        var finalCount = await database.SortedSetLengthAsync(scheduledKey);
        finalCount.ShouldBe(0, "Message should have been moved from scheduled set to stream");

        // Verify it's now in the stream
        var streamLength = await database.StreamLengthAsync(streamKey);
        streamLength.ShouldBeGreaterThan(0, "Message should be in the stream");

        _output.WriteLine($"✓ Scheduled retry was picked up by polling and moved to stream");
        _output.WriteLine($"  Initial scheduled count: {initialCount}");
        _output.WriteLine($"  Final scheduled count: {finalCount}");
        _output.WriteLine($"  Stream length: {streamLength}");
    }
}

public record TestMessage(string Content);

public class TestMessageHandler
{
    public void Handle(TestMessage message)
    {
        // Simple handler for tests
    }
}

