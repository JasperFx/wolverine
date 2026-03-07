using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using System.Collections.Concurrent;
using Wolverine.RateLimiting;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

public class rate_limiting_end_to_end(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly ConditionPoller _poller = new(output, maxRetries: 20, retryDelay: 1000.Milliseconds());
    private readonly RateLimit _limit = new(1, 2.Seconds());
    private IHost _host = null!;
    private RedisRateLimitTracker _tracker = null!;

    public async Task InitializeAsync()
    {
        var streamKey = $"rate-limit-{Guid.NewGuid():N}";
        var groupName = $"rate-limit-group-{Guid.NewGuid():N}";

        _tracker = new RedisRateLimitTracker(output);
        var endpointUri = new Uri($"redis://stream/0/{streamKey}");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(rate_limiting_end_to_end).Assembly;
                opts.Services.AddSingleton(_tracker);

                opts.UseRedisTransport("localhost:6379").AutoProvision();
                opts.PublishAllMessages().ToRedisStream(streamKey);
                opts.ListenToRedisStream(streamKey, groupName)
                    .UseDurableInbox()
                    .StartFromBeginning();

                opts.RateLimitEndpoint(endpointUri, _limit);
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task rate_limited_messages_are_delayed_with_native_scheduling()
    {
        RedisRateLimitedMessage[] messages = [
            new RedisRateLimitedMessage("A"),
            new RedisRateLimitedMessage("B")
        ];
        var bus = _host.MessageBus();

        output.WriteLine("{0} Sending message: {1}", DateTime.UtcNow, messages[0]);
        await bus.PublishAsync(messages[0]);
        output.WriteLine("{0} Sending message: {1}", DateTime.UtcNow, messages[1]);
        await bus.PublishAsync(messages[1]);

        await _poller.WaitForAsync("handled at least 2 messages",
            () => _tracker.HandledMessages.Count >= 2);

        var handled = _tracker.HandledMessages;
        handled.Select(x => x.Message).ShouldBe(messages, ignoreOrder: true);

        var buckets = handled.Select(x => RateLimitBucket.For(_limit, x.TimeStamp)).ToArray();
        buckets[0].WindowStart.ShouldBeLessThan(buckets[1].WindowStart);
    }
}

public record RedisRateLimitedMessage(string Id);

public class RedisRateLimitedMessageHandler(RedisRateLimitTracker tracker)
{
    private readonly RedisRateLimitTracker _tracker = tracker;

    public Task Handle(RedisRateLimitedMessage message, CancellationToken cancellationToken)
    {
        _tracker.RecordHandled(message);
        return Task.CompletedTask;
    }
}

public class RedisRateLimitTracker(ITestOutputHelper output)
{
    private readonly ITestOutputHelper output = output;
    private readonly ConcurrentQueue<(DateTime, RedisRateLimitedMessage)> _handledMessages = [];

    public IReadOnlyCollection<(DateTime TimeStamp, RedisRateLimitedMessage Message)> HandledMessages => _handledMessages;

    public void RecordHandled(RedisRateLimitedMessage message)
    {
        var now = DateTime.UtcNow;
        _handledMessages.Enqueue((now, message));
        output.WriteLine("{0} Handled message {1}", now, message);
    }
}
