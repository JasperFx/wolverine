using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using JasperFx.Resources;
using Wolverine.Configuration;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// GH-4521. When XREADGROUP failed with NOGROUP / "no such key" and AutoProvision was off, the listener
/// logged once at Error, set itself Stopped, and cancelled its own token. No exception reached the host,
/// nothing observable changed, and messages on that stream were never read again until the next restart --
/// even if an operator recreated the group a minute later. It now rides GH-4215's EntityMissing machinery,
/// which is the surface health checks, wolverine-diagnostics and CritterWatch already read.
/// </summary>
public class missing_consumer_group_is_observable_4521
{
    [Fact]
    public void nogroup_is_classified_as_a_missing_entity()
    {
        RedisStreamListener.IsMissingStreamOrGroup(
                new RedisServerException("NOGROUP No such key 'orders' or consumer group 'g1'"))
            .ShouldBeTrue();

        RedisStreamListener.IsMissingStreamOrGroup(new RedisServerException("ERR no such key"))
            .ShouldBeTrue();
    }

    [Fact]
    public void an_ordinary_failure_is_not_a_missing_entity()
    {
        // These must keep the loop's normal log-and-backoff policy rather than EntityMissing, or a real
        // outage would be reported as a deleted stream.
        RedisStreamListener.IsMissingStreamOrGroup(new RedisServerException("LOADING Redis is loading the dataset"))
            .ShouldBeFalse();

        RedisStreamListener.IsMissingStreamOrGroup(new InvalidOperationException("NOGROUP"))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task a_group_dropped_at_runtime_shows_up_on_the_health_snapshot_and_heals()
    {
        var streamKey = $"nogroup-{Guid.NewGuid():N}";
        const string group = "g1";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // AutoProvision is deliberately OFF: this is the path that used to go permanently silent.
                // AddResourceSetupOnStartup creates the stream and group once, up front.
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString);
                opts.ListenToRedisStream(streamKey, group).BlockTimeout(100.Milliseconds());
                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        ReceiveLoopStatus StatusOf() => runtime.Endpoints.CollectEndpointHealth()
            .Where(x => x.Direction == EndpointDirection.Listening)
            .Where(x => x.Uri.OriginalString.Contains(streamKey))
            .Select(x => x.ReceiveLoopStatus)
            .FirstOrDefault();

        await waitForAsync(() => StatusOf() == ReceiveLoopStatus.Running, 15.Seconds(),
            "the listener to report a running receive loop");

        // Drop the consumer group out from under the running listener, the way an operator would -- or a
        // Redis / emulator restarted empty.
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);
        await multiplexer.GetDatabase().StreamDeleteConsumerGroupAsync(streamKey, group);

        // The listener no longer goes silent: the loop stays alive and the health snapshot says why it
        // cannot read.
        await waitForAsync(() => StatusOf() == ReceiveLoopStatus.EntityMissing, 30.Seconds(),
            "the health snapshot to report EntityMissing");

        // ...and because the loop is still running, recreating the group heals it without a host restart.
        // That is the whole point -- the old code had cancelled its own token and could never come back.
        await multiplexer.GetDatabase().StreamCreateConsumerGroupAsync(streamKey, group, "0-0", true);

        await waitForAsync(() => StatusOf() == ReceiveLoopStatus.Running, 30.Seconds(),
            "the listener to heal back to Running");
    }

    private static async Task waitForAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException($"Timed out after {timeout} waiting for {description}");
    }
}
