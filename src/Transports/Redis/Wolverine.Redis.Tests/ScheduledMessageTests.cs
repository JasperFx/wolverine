using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Redis.Tests;

[Collection("ScheduledMessageTests")]
public class ScheduledMessageTests
{
    private async Task<IHost> CreateHostAsync()
    {
        var streamKey = $"scheduled-test-{Guid.NewGuid():N}";
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ScheduledTestService";
                
                // Fast polling for tests
                opts.Durability.ScheduledJobFirstExecution = 50.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                
                opts.PublishAllMessages().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "scheduled-test-group")
                    .StartFromBeginning();
                    
                opts.Services.AddSingleton<ScheduledMessageTracker>();
            }).StartAsync();
    }

    [Fact]
    public async Task should_send_scheduled_message_immediately_when_schedule_time_is_past()
    {
        using var host = await CreateHostAsync();
        var tracker = host.Services.GetRequiredService<ScheduledMessageTracker>();
        var bus = host.MessageBus();

        var command = new ScheduledTestCommand(Guid.NewGuid().ToString());
        
        // Schedule for 1 second ago (should execute immediately)
        await bus.ScheduleAsync(command, DateTimeOffset.UtcNow.AddSeconds(-1));
        
        // Wait for message to be processed
        await Task.Delay(2000);
        
        tracker.ReceivedMessages.ShouldContain(command.Id);
    }

    [Fact]
    public async Task should_delay_execution_of_scheduled_message()
    {
        using var host = await CreateHostAsync();
        var tracker = host.Services.GetRequiredService<ScheduledMessageTracker>();
        var bus = host.MessageBus();

        var command = new ScheduledTestCommand(Guid.NewGuid().ToString());
        var scheduledTime = DateTimeOffset.UtcNow.AddSeconds(3);
        
        var startTime = DateTimeOffset.UtcNow;
        await bus.ScheduleAsync(command, scheduledTime);
        
        // Wait a bit less than scheduled time - should not be processed yet
        await Task.Delay(1500);
        tracker.ReceivedMessages.ShouldNotContain(command.Id);
        
        // Wait for the message to be processed after the scheduled time
        await Task.Delay(3000);
        
        tracker.ReceivedMessages.ShouldContain(command.Id);
        var executionTime = tracker.GetExecutionTime(command.Id);
        
        // Verify it was executed after the scheduled time (with some tolerance for polling interval)
        executionTime.ShouldBeGreaterThanOrEqualTo(scheduledTime.AddSeconds(-2));
    }

    [Fact]
    public async Task should_handle_multiple_scheduled_messages_at_different_times()
    {
        using var host = await CreateHostAsync();
        var tracker = host.Services.GetRequiredService<ScheduledMessageTracker>();
        var bus = host.MessageBus();

        var command1 = new ScheduledTestCommand(Guid.NewGuid().ToString());
        var command2 = new ScheduledTestCommand(Guid.NewGuid().ToString());
        var command3 = new ScheduledTestCommand(Guid.NewGuid().ToString());
        
        // Schedule messages at different times
        await bus.ScheduleAsync(command1, DateTimeOffset.UtcNow.AddSeconds(2));
        await bus.ScheduleAsync(command2, DateTimeOffset.UtcNow.AddSeconds(4));
        await bus.ScheduleAsync(command3, DateTimeOffset.UtcNow.AddSeconds(1));
        
        // Wait for all messages to be processed
        await Task.Delay(6000);
        
        tracker.ReceivedMessages.ShouldContain(command1.Id);
        tracker.ReceivedMessages.ShouldContain(command2.Id);
        tracker.ReceivedMessages.ShouldContain(command3.Id);
    }

    [Fact]
    public async Task scheduled_messages_are_stored_in_redis_sorted_set()
    {
        var streamKey = $"scheduled-test-{Guid.NewGuid():N}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ScheduledTestService";
                
                // Fast polling for tests
                opts.Durability.ScheduledJobFirstExecution = 50.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                
                opts.PublishAllMessages().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "scheduled-test-group")
                    .StartFromBeginning();
                    
                opts.Services.AddSingleton<ScheduledMessageTracker>();
            }).StartAsync();
        
        // This test verifies that scheduled messages use Redis sorted sets
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        
        var endpoint = transport.StreamEndpoint(streamKey);
        var database = transport.GetDatabase(database: 0);
        
        var scheduledKey = endpoint.ScheduledMessagesKey;
        
        // Schedule a message
        var bus = host.MessageBus();
        var command = new ScheduledTestCommand(Guid.NewGuid().ToString());
        await bus.ScheduleAsync(command, DateTimeOffset.UtcNow.AddSeconds(10));
        
        // Wait a bit for it to be persisted
        await Task.Delay(500);
        
        // Verify it's in the scheduled sorted set
        var count = await database.SortedSetLengthAsync(scheduledKey);
        count.ShouldBeGreaterThan(0, "Scheduled message should be in Redis sorted set");
    }
}

public record ScheduledTestCommand(string Id);

public class ScheduledTestCommandHandler
{
    private readonly ScheduledMessageTracker _tracker;

    public ScheduledTestCommandHandler(ScheduledMessageTracker tracker)
    {
        _tracker = tracker;
    }

    public void Handle(ScheduledTestCommand command)
    {
        _tracker.RecordExecution(command.Id);
    }
}

public class ScheduledMessageTracker
{
    private readonly List<string> _receivedMessages = new();
    private readonly Dictionary<string, DateTimeOffset> _executionTimes = new();
    private readonly object _lock = new();

    public IReadOnlyList<string> ReceivedMessages
    {
        get
        {
            lock (_lock)
            {
                return _receivedMessages.ToList();
            }
        }
    }

    public void RecordExecution(string messageId)
    {
        lock (_lock)
        {
            _receivedMessages.Add(messageId);
            _executionTimes[messageId] = DateTimeOffset.UtcNow;
        }
    }

    public DateTimeOffset GetExecutionTime(string messageId)
    {
        lock (_lock)
        {
            return _executionTimes[messageId];
        }
    }
}

