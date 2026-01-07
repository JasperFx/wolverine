using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Wolverine.ErrorHandling;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

[Collection("RetryLimitTests")]
public class RetryLimitTests
{
    private readonly ITestOutputHelper _output;

    public RetryLimitTests(ITestOutputHelper _output)
    {
        this._output = _output;
    }

    [Fact]
    public async Task retries_should_stop_after_configured_limit()
    {
        var streamKey = $"retry-limit-{Guid.NewGuid():N}";
        
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "RetryLimitTestService";
                
                // Configure fast polling for test
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 200.Milliseconds();
                
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                
                // Configure routing to our test stream
                opts.PublishMessage<AlwaysFailingCommand>().ToRedisStream(streamKey);
                
                // Listen to the stream - it will be Durable mode by default for Redis streams
                opts.ListenToRedisStream(streamKey, "retry-limit-group")
                    .StartFromBeginning();
                
                // Configure a retry policy with ONLY 2 retries (3 total attempts)
                opts.Policies.OnException<InvalidOperationException>()
                    .ScheduleRetry(500.Milliseconds(), 500.Milliseconds());
                
                // Register the handler
                opts.Discovery.IncludeType<AlwaysFailingCommandHandler>();
                
                opts.Services.AddSingleton<RetryLimitTracker>();
            }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        var database = transport.GetDatabase(database: endpoint.DatabaseId);
        var scheduledKey = endpoint.ScheduledMessagesKey;
        var deadLetterKey = endpoint.DeadLetterQueueKey;

        // Clear everything
        await database.KeyDeleteAsync(scheduledKey);
        await database.KeyDeleteAsync(streamKey);
        await database.KeyDeleteAsync(deadLetterKey);

        var tracker = host.Services.GetRequiredService<RetryLimitTracker>();

        // Wait for listener to fully initialize
        await Task.Delay(500);

        // Send a message that will ALWAYS fail
        var bus = host.MessageBus();
        var command = new AlwaysFailingCommand(Guid.NewGuid().ToString());
        
        _output.WriteLine($"Sending command: {command.Id}");
        await bus.PublishAsync(command);

        // Wait for first attempt and first retry
        await Task.Delay(1500);
        _output.WriteLine($"After 1.5s - Handler calls: {tracker.AttemptCount}");
        
        // Check scheduled set
        var scheduledCount1 = await database.SortedSetLengthAsync(scheduledKey);
        _output.WriteLine($"  Scheduled messages: {scheduledCount1}");
        
        // Wait for second retry
        await Task.Delay(1500);
        _output.WriteLine($"After 3s - Handler calls: {tracker.AttemptCount}");
        
        var scheduledCount2 = await database.SortedSetLengthAsync(scheduledKey);
        _output.WriteLine($"  Scheduled messages: {scheduledCount2}");

        // Inspect scheduled messages
        if (scheduledCount2 > 0)
        {
            var entries = await database.SortedSetRangeByScoreWithScoresAsync(scheduledKey);
            _output.WriteLine($"  Scheduled entries: {entries.Length}");
            foreach (var entry in entries)
            {
                var env = Wolverine.Runtime.Serialization.EnvelopeSerializer.Deserialize((byte[])entry.Element!);
                var retryTime = DateTimeOffset.FromUnixTimeMilliseconds((long)entry.Score);
                _output.WriteLine($"    - Envelope {env.Id}, Attempts={env.Attempts}, Score={entry.Score}, RetryTime={retryTime}");
            }
        }
        
        // Wait longer for any remaining retries
        await Task.Delay(3000);
        
        var finalAttemptCount = tracker.AttemptCount;
        _output.WriteLine($"After 6s - Handler calls: {finalAttemptCount}");
        
        var scheduledCountFinal = await database.SortedSetLengthAsync(scheduledKey);
        _output.WriteLine($"  Scheduled messages: {scheduledCountFinal}");
        
        // Log all attempt numbers
        _output.WriteLine($"Attempt sequence: {string.Join(", ", tracker.AttemptNumbers)}");
        
        // Should be exactly 3: initial + 2 retries
        finalAttemptCount.ShouldBe(3, "Handler should be called exactly 3 times (initial + 2 retries)");

        // Check if message moved to dead letter queue
        var deadLetterCount = await database.StreamLengthAsync(deadLetterKey);
        _output.WriteLine($"Dead letter queue count: {deadLetterCount}");
        
        if (finalAttemptCount < 3)
        {
            _output.WriteLine($"⚠ ISSUE: Only {finalAttemptCount} attempts occurred, expected 3");
            _output.WriteLine("  This could mean retries are stopping too early OR not being processed");
        }
        else if (finalAttemptCount > 3)
        {
            _output.WriteLine($"⚠ ISSUE: {finalAttemptCount} attempts occurred, expected only 3");
            _output.WriteLine("  This means retries are NOT stopping after the configured limit!");
        }
        
        if (deadLetterCount > 0)
        {
            _output.WriteLine("✓ Message was moved to dead letter queue");
        }
        else
        {
            _output.WriteLine("⚠ Message was NOT moved to dead letter queue");
        }
    }
}

public record AlwaysFailingCommand(string Id);

public class AlwaysFailingCommandHandler
{
    private readonly RetryLimitTracker _tracker;

    public AlwaysFailingCommandHandler(RetryLimitTracker tracker)
    {
        _tracker = tracker;
    }

    public void Handle(AlwaysFailingCommand command)
    {
        var attempt = _tracker.RecordAttempt();
        // Always throw - never succeed
        throw new InvalidOperationException($"Always fails! Attempt {attempt} for command {command.Id}");
    }
}

public class RetryLimitTracker
{
    private int _attemptCount = 0;
    private readonly object _lock = new();
    private readonly List<int> _attemptNumbers = new();

    public int AttemptCount
    {
        get
        {
            lock (_lock)
            {
                return _attemptCount;
            }
        }
    }

    public List<int> AttemptNumbers
    {
        get
        {
            lock (_lock)
            {
                return new List<int>(_attemptNumbers);
            }
        }
    }

    public int RecordAttempt()
    {
        lock (_lock)
        {
            _attemptCount++;
            _attemptNumbers.Add(_attemptCount);
            return _attemptCount;
        }
    }
}

