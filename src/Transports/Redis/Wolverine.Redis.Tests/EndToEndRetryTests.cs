using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

[Collection("EndToEndRetryTests")]
public class EndToEndRetryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task message_with_retry_policy_saves_to_redis_and_retries()
    {
        var streamKey = $"e2e-retry-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "E2ERetryTestService";

                // Configure fast polling for test
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 200.Milliseconds();

                opts.UseRedisTransport("localhost:6379").AutoProvision();

                // Configure routing to our test stream (without SendInline to ensure durable processing)
                opts.PublishMessage<E2EFailingCommand>().ToRedisStream(streamKey);

                opts.ListenToRedisStream(streamKey, "e2e-retry-group")
                    .UseDurableInbox() // Use Durable endpoint (for Redis streams BufferedInMemory is default)
                    .StartFromBeginning();

                // Configure a retry policy
                opts.Policies.OnException<InvalidOperationException>()
                    .ScheduleRetry(1.Seconds());

                // Register the handler
                opts.Discovery.IncludeType<E2EFailingCommandHandler>();

                opts.Services.AddSingleton<E2ERetryTracker>();
            }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        var database = transport.GetDatabase(database: endpoint.DatabaseId);
        var scheduledKey = endpoint.ScheduledMessagesKey;

        endpoint.Mode.ShouldBe(EndpointMode.Durable, "Endpoint should be in Durable mode for retries to work");
        endpoint.ShouldBeAssignableTo<IDatabaseBackedEndpoint>("Endpoint should implement IDatabaseBackedEndpoint");

        // Clear the scheduled set
        await database.KeyDeleteAsync(scheduledKey);
        await database.KeyDeleteAsync(streamKey);

        var tracker = host.Services.GetRequiredService<E2ERetryTracker>();
        tracker.FailCount = 1; // Fail once, then succeed

        // Send a message that will fail 
        var bus = host.MessageBus();
        var command = new E2EFailingCommand(Guid.NewGuid().ToString());

        output.WriteLine($"Sending command: {command.Id}");
        await bus.PublishAsync(command);

        // Check if the message was actually sent to the stream
        await WaitForAsync("message is sent", async () => tracker.AttemptCount == 1);
        var streamLength = await database.StreamLengthAsync(streamKey);
        tracker.AttemptCount.ShouldBe(1, "Handler should have been called once");

        // Verify the message is in the scheduled set (waiting for retry)
        long scheduledCount = 0;
        await WaitForAsync("message is in the scheduled set for retry", async () =>
        {
            scheduledCount = await database.SortedSetLengthAsync(scheduledKey);
            return scheduledCount == 1;
        });
        scheduledCount.ShouldBe(1, "Message should be in the scheduled set for retry");

        // Verify the score is in the future (retry delay)
        var entries = await database.SortedSetRangeByScoreWithScoresAsync(scheduledKey);
        entries.Length.ShouldBe(1, "There should be one entry in the scheduled set");
        var retryTime = DateTimeOffset.FromUnixTimeMilliseconds((long)entries[0].Score);
        retryTime.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMilliseconds(-500), "Retry should be scheduled in the future");

        // Verify the handler was called again (retry)
        await WaitForAsync("handler is called again for retry", async () => tracker.AttemptCount == 2);
        tracker.AttemptCount.ShouldBe(2, "Handler should be called twice: initial attempt + retry");
        tracker.LastFailed.ShouldBeFalse();

        // Verify the scheduled set is now empty
        var finalScheduledCount = await database.SortedSetLengthAsync(scheduledKey);
        finalScheduledCount.ShouldBe(0, "Message should have been removed from scheduled set after retry");
    }

    private async Task WaitForAsync(string message, Func<ValueTask<bool>> condition, int delayMs = 50, int maxRetries = 100)
    {
        var i = 0;
        while (!await condition() && i < maxRetries)
        {
            i++;
            output.WriteLine($"Waiting for condition: {message} (total {i * delayMs}ms)...");
            await Task.Delay(delayMs);
        }
    }
}

public record E2EFailingCommand(string Id);

public class E2EFailingCommandHandler(E2ERetryTracker tracker)
{
    public void Handle(E2EFailingCommand command)
    {
        var attempt = tracker.RecordAttempt();

        if (attempt <= tracker.FailCount)
        {
            throw new InvalidOperationException($"Intentional failure on attempt {attempt} for command {command.Id}");
        }

        // Success
    }
}

public class E2ERetryTracker
{
    private int _attemptCount = 0;
    private readonly object _lock = new();
    public bool LastFailed { get; set; }

    public int FailCount { get; set; } = 0;

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

    public int RecordAttempt()
    {
        lock (_lock)
        {
            _attemptCount++;
            LastFailed = _attemptCount <= FailCount;
            return _attemptCount;
        }
    }
}
