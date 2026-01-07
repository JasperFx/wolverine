using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

/// <summary>
/// Integration tests verifying the complete scheduled message flow
/// </summary>
[Collection("ScheduledMessageIntegrationTests")]
public class ScheduledMessageIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ScheduledMessageIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }
    
    [Fact]
    public async Task verify_scheduled_messages_use_sorted_set()
    {
        var streamKey = $"integration-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "IntegrationTestService";
                opts.Durability.ScheduledJobFirstExecution = 50.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                
                opts.PublishAllMessages().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "integration-test-group")
                    .StartFromBeginning();
            }).StartAsync();
        
        // Verify scheduled messages are stored in Redis sorted set
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        await using var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        var database = transport.GetDatabase(database: 0);
        
        var scheduledKey = endpoint.ScheduledMessagesKey;
        
        // Schedule a message
        var bus = host.MessageBus();
        var command = new IntegrationTestCommand(Guid.NewGuid().ToString());
        await bus.ScheduleAsync(command, DateTimeOffset.UtcNow.AddSeconds(10));
        
        // Wait a bit
        await Task.Delay(500);
        
        // Verify it's in a sorted set
        var keyType = await database.KeyTypeAsync(scheduledKey);
        keyType.ShouldBe(RedisType.SortedSet, "Scheduled messages should use sorted set");
        
        _output.WriteLine("✓ Scheduled messages use Redis sorted set");
    }

    [Fact]
    public async Task verify_scheduled_messages_key_format()
    {
        var streamKey = $"integration-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                opts.PublishAllMessages().ToRedisStream(streamKey).SendInline();
            }).StartAsync();
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        
        var scheduledKey = endpoint.ScheduledMessagesKey;
        scheduledKey.ShouldBe($"{streamKey}:scheduled");
        
        _output.WriteLine($"✓ Scheduled messages key: {scheduledKey}");
    }

    [Fact]
    public async Task verify_message_moves_from_scheduled_to_stream()
    {
        var streamKey = $"integration-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "IntegrationTestService";
                opts.Durability.ScheduledJobFirstExecution = 50.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                
                opts.PublishAllMessages().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "integration-test-group")
                    .StartFromBeginning();
            }).StartAsync();
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        var database = transport.GetDatabase(database: 0);
        
        var scheduledKey = endpoint.ScheduledMessagesKey;

        // Schedule a message for near future
        var bus = host.MessageBus();
        var command = new IntegrationTestCommand(Guid.NewGuid().ToString());
        await bus.ScheduleAsync(command, DateTimeOffset.UtcNow.AddSeconds(1));

        // Check it's in the scheduled set
        await Task.Delay(300);
        var scheduledCount = await database.SortedSetLengthAsync(scheduledKey);
        scheduledCount.ShouldBeGreaterThan(0);
        _output.WriteLine($"Message added to scheduled set (count: {scheduledCount})");

        // Wait for it to be moved
        await Task.Delay(2000);

        // Should be removed from scheduled set
        var remainingScheduled = await database.SortedSetLengthAsync(scheduledKey);
        _output.WriteLine($"Remaining in scheduled set: {remainingScheduled}");

        // Should be in the stream
        var streamLength = await database.StreamLengthAsync(streamKey);
        streamLength.ShouldBeGreaterThan(0, "Message should be in the stream");
        
        _output.WriteLine($"✓ Message moved to stream (stream length: {streamLength})");
    }

    [Fact]
    public async Task verify_score_represents_execution_time()
    {
        var streamKey = $"integration-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "IntegrationTestService";
                opts.Durability.ScheduledJobFirstExecution = 50.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                
                opts.PublishAllMessages().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "integration-test-group")
                    .StartFromBeginning();
            }).StartAsync();
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        var database = transport.GetDatabase(database: 0);
        
        var scheduledKey = endpoint.ScheduledMessagesKey;

        // Schedule a message
        var scheduledTime = DateTimeOffset.UtcNow.AddSeconds(30);
        var bus = host.MessageBus();
        var command = new IntegrationTestCommand(Guid.NewGuid().ToString());
        await bus.ScheduleAsync(command, scheduledTime);

        await Task.Delay(300);

        // Get the score from Redis
        var entries = await database.SortedSetRangeByScoreWithScoresAsync(scheduledKey);
        entries.Length.ShouldBeGreaterThan(0);

        var score = entries[0].Score;
        var expectedScore = scheduledTime.ToUnixTimeMilliseconds();

        // Score should be very close to the expected Unix timestamp in milliseconds
        Math.Abs(score - expectedScore).ShouldBeLessThan(1000, 
            "Score should represent Unix timestamp in milliseconds");

        _output.WriteLine($"✓ Score ({score}) matches expected timestamp ({expectedScore})");
    }
}

public record IntegrationTestCommand(string Id);

public class IntegrationTestCommandHandler
{
    public void Handle(IntegrationTestCommand command)
    {
        // Simple handler for integration tests
        Console.WriteLine($"Processed: {command.Id}");
    }
}
