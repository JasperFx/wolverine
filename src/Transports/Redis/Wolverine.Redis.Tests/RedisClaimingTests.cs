using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Shouldly;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Redis.Tests;

public class RedisClaimingTests
{
    public record TestMessage(string Id);

    private static async Task<IDatabase> ConnectAsync() => (await ConnectionMultiplexer.ConnectAsync("localhost:6379")).GetDatabase();

    [Fact]
    public async Task claim_and_process_pending_messages()
    {
        var streamKey = $"wolverine-tests-claim-{Guid.NewGuid():N}";
        var group = "g1";
        var consumerA = "A";

        var db = await ConnectAsync();

        // Ensure group exists
        try { await db.StreamCreateConsumerGroupAsync(streamKey, group, "$", true); } catch { }

        // Produce one message
        var json = "{\"Id\":\"abc\"}";
        var typeName = typeof(TestMessage).ToMessageTypeName();
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
                var endpoint = opts.ListenToRedisStream(streamKey, group);
                endpoint.EnableAutoClaim(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1));
                endpoint.BatchSize = 10;
                endpoint.BlockTimeoutMilliseconds = 100;
                endpoint.MessageType = typeof(TestMessage);

                opts.Services.AddSingleton(tcs);
            })
            .StartAsync();

        // Wait up to 10 seconds for message to be handled via claim loop
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.ShouldBe(tcs.Task);
        var result = await tcs.Task;
        result.ShouldBeTrue();
    }
}

public class TestHandler
{
    private readonly TaskCompletionSource<bool> _tcs;
    public TestHandler(TaskCompletionSource<bool> tcs) => _tcs = tcs;

    public void Handle(RedisClaimingTests.TestMessage msg)
    {
        _tcs.TrySetResult(true);
    }
}

