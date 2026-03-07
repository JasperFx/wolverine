using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

[Collection("EndToEndRetryTests")]
public class EndToEndRetryTests(ITestOutputHelper output): IAsyncLifetime
{
    private readonly ConditionPoller _poller = new(output, maxRetries: 20, retryDelay: 500.Milliseconds());
    private RedisStreamEndpoint _endpoint = null!;
    private IDatabase _database = null!;
    private string _scheduledKey = null!;
    private string _streamKey = null!;
    private IHost _host = null!;
    private E2ERetryTracker _tracker = null!;

    public async Task InitializeAsync()
    {
        _streamKey = $"e2e-retry-{Guid.NewGuid():N}";

        _tracker = new E2ERetryTracker();
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "E2ERetryTestService";

                // Configure fast polling for test
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 200.Milliseconds();

                opts.UseRedisTransport("localhost:6379").AutoProvision();

                // Configure routing to our test stream (without SendInline to ensure durable processing)
                opts.PublishMessage<E2EFailingCommand>().ToRedisStream(_streamKey);

                opts.ListenToRedisStream(_streamKey, "e2e-retry-group")
                    .UseDurableInbox() // Use Durable endpoint (for Redis streams BufferedInMemory is default)
                    .StartFromBeginning();

                // Configure a retry policy
                opts.Policies.OnException<InvalidOperationException>()
                    .ScheduleRetry(1.Seconds());

                // Register the handler
                opts.Discovery.IncludeType<E2EFailingCommandHandler>();

                opts.Services.AddSingleton(_tracker);
            }).StartAsync();

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();

        _endpoint = transport.StreamEndpoint(_streamKey);
        _database = transport.GetDatabase(database: _endpoint.DatabaseId);
        _scheduledKey = _endpoint.ScheduledMessagesKey;
        await DeleteDatabaseKeys();
    }

    public async Task DisposeAsync()
    {
        await DeleteDatabaseKeys();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task message_with_retry_policy_saves_to_redis_and_retries()
    {
        _endpoint.Mode.ShouldBe(EndpointMode.Durable, "Endpoint should be in Durable mode for retries to work");
        _endpoint.ShouldBeAssignableTo<IDatabaseBackedEndpoint>("Endpoint should implement IDatabaseBackedEndpoint");

        _tracker.FailCount = 1; // Fail once, then succeed

        // Send a message that will fail 
        var bus = _host.MessageBus();
        var message = new E2EFailingCommand(Guid.NewGuid().ToString());

        output.WriteLine("{0} Sending message: {1}", DateTime.UtcNow, message);
        await bus.PublishAsync(message);

        // Check if the message was actually sent to the stream
        await _poller.WaitForAsync("message is sent", () => _tracker.AttemptCount == 1);
        var streamLength = await _database.StreamLengthAsync(_streamKey);
        _tracker.AttemptCount.ShouldBe(1, "Handler should have been called once");

        // Verify the message is in the scheduled set (waiting for retry)
        long scheduledCount = 0;
        await _poller.WaitForAsync("message is in the scheduled set for retry", async () =>
        {
            scheduledCount = await _database.SortedSetLengthAsync(_scheduledKey);
            return scheduledCount == 1;
        });
        scheduledCount.ShouldBe(1, "Message should be in the scheduled set for retry");

        // Verify the score is in the future (retry delay)
        var entries = await _database.SortedSetRangeByScoreWithScoresAsync(_scheduledKey);
        entries.Length.ShouldBe(1, "There should be one entry in the scheduled set");
        var retryTime = DateTimeOffset.FromUnixTimeMilliseconds((long)entries[0].Score);
        retryTime.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMilliseconds(-500), "Retry should be scheduled in the future");

        // Verify the handler was called again (retry)
        await _poller.WaitForAsync("handler is called again for retry", () => _tracker.AttemptCount == 2);
        _tracker.AttemptCount.ShouldBe(2, "Handler should be called twice: initial attempt + retry");
        _tracker.LastFailed.ShouldBeFalse();

        // Verify the scheduled set is now empty
        var finalScheduledCount = await _database.SortedSetLengthAsync(_scheduledKey);
        finalScheduledCount.ShouldBe(0, "Message should have been removed from scheduled set after retry");
    }

    private async Task DeleteDatabaseKeys()
    {
        await _database.KeyDeleteAsync(_scheduledKey);
        await _database.KeyDeleteAsync(_streamKey);
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
