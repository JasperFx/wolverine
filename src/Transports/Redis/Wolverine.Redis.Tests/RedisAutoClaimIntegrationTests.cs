using System.Text;
using System.Threading.Tasks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Shouldly;
using Wolverine;
using Wolverine.Redis;
using Wolverine.Redis.Internal;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Redis.Tests;

public class RedisAutoClaimIntegrationTests
{
    public record AutoClaimTestMessage(string Id);

    private static async Task<IDatabase> ConnectAsync() => (await ConnectionMultiplexer.ConnectAsync("localhost:6379")).GetDatabase();

    [Fact]
    public async Task autoclaim_integration_processes_pending_messages()
    {
        var streamKey = $"wolverine-tests-autoclaim-{Guid.NewGuid():N}";
        var group = "g1";
        var consumerA = "A";

        var db = await ConnectAsync();

        // Ensure group exists
        try { await db.StreamCreateConsumerGroupAsync(streamKey, group, "$", true); } catch { }

        // Produce one message
        var json = "{\"Id\":\"auto-claim-test\"}";
        var typeName = typeof(AutoClaimTestMessage).ToMessageTypeName();
        await db.StreamAddAsync(streamKey, new[]
        {
            new NameValueEntry("payload", (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(json)),
            new NameValueEntry("wolverine-message-type", typeName),
            new NameValueEntry("wolverine-content-type", "application/json")
        });

        // Read with consumer A but do not ack, to create a pending entry
        await db.StreamReadGroupAsync(streamKey, group, consumerA, ">", 1, false);
        // Give the message a little idle time to satisfy minIdle
        await Task.Delay(250);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379");
                opts
                    .ListenToRedisStream(streamKey, group)
                    .EnableAutoClaim(500.Milliseconds(), 100.Milliseconds())
                    .BlockTimeout(100.Milliseconds())
                    .DefaultIncomingMessage<AutoClaimTestMessage>();
                
                opts.Services.AddSingleton(tcs);
            })
            .StartAsync();

        // Wait up to 10 seconds for message to be handled via auto-claim
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.ShouldBe(tcs.Task);
        var result = await tcs.Task;
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task autoclaim_disabled_by_default()
    {
        var streamKey = $"wolverine-tests-autoclaim-disabled-{Guid.NewGuid():N}";
        var group = "g1";

        var db = await ConnectAsync();

        // Ensure group exists
        try { await db.StreamCreateConsumerGroupAsync(streamKey, group, "$", true); } catch { }

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379");
                var expression = opts.ListenToRedisStream(streamKey, group).DefaultIncomingMessage<AutoClaimTestMessage>();

                var endpoint = expression.Endpoint.As<RedisStreamEndpoint>();
                
                // AutoClaim should be disabled by default
                endpoint.AutoClaimEnabled.ShouldBeFalse();
                endpoint.AutoClaimPeriod.ShouldBe(TimeSpan.FromSeconds(30)); // Default period
            })
            .StartAsync();

        await Task.Delay(100); // Brief delay to let host start
    }

    [Fact]
    public void fluent_api_enables_autoclaim_with_custom_settings()
    {
        var transport = new RedisTransport("localhost:6379");
        var endpoint = transport.StreamEndpoint("test");
        
        endpoint.AutoClaimEnabled.ShouldBeFalse(); // Default
        endpoint.AutoClaimPeriod.ShouldBe(TimeSpan.FromSeconds(30)); // Default
        endpoint.AutoClaimMinIdle.ShouldBe(TimeSpan.FromMinutes(1)); // Default

        new RedisListenerConfiguration(endpoint).EnableAutoClaim(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2));

        endpoint.AutoClaimEnabled.ShouldBeTrue();
        endpoint.AutoClaimPeriod.ShouldBe(TimeSpan.FromSeconds(15));
        endpoint.AutoClaimMinIdle.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void fluent_api_disables_autoclaim()
    {
        var transport = new RedisTransport("localhost:6379");
        var endpoint = transport.StreamEndpoint("test");

        new RedisListenerConfiguration(endpoint).EnableAutoClaim().DisableAutoClaim();

        endpoint.AutoClaimEnabled.ShouldBeFalse();
    }
}

public class AutoClaimTestHandler
{
    private readonly TaskCompletionSource<bool> _tcs;
    public AutoClaimTestHandler(TaskCompletionSource<bool> tcs) => _tcs = tcs;

    public void Handle(RedisAutoClaimIntegrationTests.AutoClaimTestMessage msg)
    {
        _tcs.TrySetResult(true);
    }
}
