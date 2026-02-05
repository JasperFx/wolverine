using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RateLimiting;
using Xunit;

namespace Wolverine.Redis.Tests;

public class rate_limiting_end_to_end
{
    [Fact]
    public async Task rate_limited_messages_are_delayed_with_native_scheduling()
    {
        var streamKey = $"rate-limit-{Guid.NewGuid():N}";
        var groupName = $"rate-limit-group-{Guid.NewGuid():N}";
        var window = 2.Seconds();
        var limit = new RateLimit(1, window);
        var tracker = new RedisRateLimitTracker(expectedCount: 2);
        var endpointUri = new Uri($"redis://stream/0/{streamKey}");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(rate_limiting_end_to_end).Assembly;
                opts.Services.AddSingleton(tracker);

                opts.UseRedisTransport("localhost:6379").AutoProvision();
                opts.PublishAllMessages().ToRedisStream(streamKey);
                opts.ListenToRedisStream(streamKey, groupName).StartFromBeginning();

                opts.RateLimitEndpoint(endpointUri, limit);
            }).StartAsync();

        await Task.Delay(250.Milliseconds());
        await waitForNextBucketStartAsync(limit);

        var bus = host.MessageBus();
        await bus.PublishAsync(new RedisRateLimitedMessage(Guid.NewGuid().ToString()));
        await bus.PublishAsync(new RedisRateLimitedMessage(Guid.NewGuid().ToString()));

        await tracker.WaitForHandledAsync(15.Seconds());

        var handled = tracker.HandledTimes;
        handled.Count.ShouldBeGreaterThanOrEqualTo(2);

        var firstBucket = RateLimitBucket.For(limit, handled[0]);
        var secondBucket = RateLimitBucket.For(limit, handled[1]);
        firstBucket.WindowStart.ShouldNotBe(secondBucket.WindowStart);
    }

    private static async Task waitForNextBucketStartAsync(RateLimit limit)
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = RateLimitBucket.For(limit, now);
        var delay = bucket.WindowEnd - now + 50.Milliseconds();
        if (delay < TimeSpan.Zero)
        {
            delay = 50.Milliseconds();
        }

        await Task.Delay(delay);
    }
}

public record RedisRateLimitedMessage(string Id);

public class RedisRateLimitedMessageHandler
{
    private readonly RedisRateLimitTracker _tracker;

    public RedisRateLimitedMessageHandler(RedisRateLimitTracker tracker)
    {
        _tracker = tracker;
    }

    public Task Handle(RedisRateLimitedMessage message, CancellationToken cancellationToken)
    {
        _tracker.RecordHandled();
        return Task.CompletedTask;
    }
}

public class RedisRateLimitTracker
{
    private readonly int _expectedCount;
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DateTimeOffset> _handledTimes = [];
    private readonly object _lock = new();

    public RedisRateLimitTracker(int expectedCount)
    {
        _expectedCount = expectedCount;
    }

    public IReadOnlyList<DateTimeOffset> HandledTimes
    {
        get
        {
            lock (_lock)
            {
                return _handledTimes.ToList();
            }
        }
    }

    public void RecordHandled()
    {
        lock (_lock)
        {
            _handledTimes.Add(DateTimeOffset.UtcNow);
            if (_handledTimes.Count >= _expectedCount)
            {
                _completion.TrySetResult(true);
            }
        }
    }

    public async Task WaitForHandledAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await _completion.Task.WaitAsync(cts.Token);
    }
}
