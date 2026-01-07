using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

[Collection("NativeSchedulingRetryTests")]
public class NativeSchedulingRetryTests
{
    private readonly ITestOutputHelper _output;

    public NativeSchedulingRetryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void redis_endpoint_implements_idatabase_backed_endpoint()
    {
        // Verify that RedisStreamEndpoint implements IDatabaseBackedEndpoint interface
        var endpointType = typeof(RedisStreamEndpoint);
        var interfaceType = typeof(IDatabaseBackedEndpoint);
        
        interfaceType.IsAssignableFrom(endpointType).ShouldBeTrue(
            "RedisStreamEndpoint should implement IDatabaseBackedEndpoint");
        
        _output.WriteLine("✓ RedisStreamEndpoint implements IDatabaseBackedEndpoint interface");
        _output.WriteLine("  This enables DurableReceiver to call ScheduleRetryAsync() for native retry scheduling");
    }

    [Fact]
    public void durable_receiver_implements_native_scheduling()
    {
        // Verify that DurableReceiver implements ISupportNativeScheduling interface
        // This is what enables the native retry scheduling flow
        var receiverType = typeof(Wolverine.Runtime.WorkerQueues.DurableReceiver);
        var interfaceType = typeof(ISupportNativeScheduling);
        
        interfaceType.IsAssignableFrom(receiverType).ShouldBeTrue(
            "DurableReceiver should implement ISupportNativeScheduling");
        
        _output.WriteLine("✓ DurableReceiver implements ISupportNativeScheduling");
        _output.WriteLine("  Combined with IDatabaseBackedEndpoint, this enables native retry scheduling");
    }

    [Fact]
    public async Task endpoint_schedule_retry_async_should_save_to_redis()
    {
        var streamKey = $"retry-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "RetryTestService";
                opts.UseRedisTransport("localhost:6379").AutoProvision();
            }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey) as IDatabaseBackedEndpoint;
        var database = transport.GetDatabase(database: 0);
        var scheduledKey = transport.StreamEndpoint(streamKey).ScheduledMessagesKey;

        endpoint.ShouldNotBeNull("Endpoint should implement IDatabaseBackedEndpoint");

        // Clear the scheduled set
        await database.KeyDeleteAsync(scheduledKey);

        // Create a test envelope
        var message = new RetryTestCommand(Guid.NewGuid().ToString());
        var envelope = new Envelope(message)
        {
            Id = Guid.NewGuid(),
            ScheduledTime = DateTimeOffset.UtcNow.AddSeconds(10),
            MessageType = typeof(RetryTestCommand).ToMessageTypeName()
        };
        
        // Serialize the message
        var writer = runtime.Options.DefaultSerializer;
        envelope.Data = writer.WriteMessage(message);
        envelope.ContentType = writer.ContentType;

        // Call ScheduleRetryAsync
        await endpoint!.ScheduleRetryAsync(envelope, CancellationToken.None);

        // Wait for Redis to persist
        await Task.Delay(200);

        // Verify the message is in the scheduled set
        var scheduledCount = await database.SortedSetLengthAsync(scheduledKey);
        scheduledCount.ShouldBe(1, "Envelope should be in scheduled sorted set");

        // Verify the score
        var entries = await database.SortedSetRangeByScoreWithScoresAsync(scheduledKey);
        entries.Length.ShouldBe(1);
        
        var expectedScore = envelope.ScheduledTime!.Value.ToUnixTimeMilliseconds();
        Math.Abs(entries[0].Score - expectedScore).ShouldBeLessThan(1000);

        _output.WriteLine($"✓ ScheduleRetryAsync saved envelope to Redis sorted set");
        _output.WriteLine($"  Score: {entries[0].Score}");
    }
}

public record RetryTestCommand(string Id);

