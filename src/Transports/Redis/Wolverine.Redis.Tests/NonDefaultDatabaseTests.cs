using System;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// Regression test for: Redis Streams listener ignores endpoint DatabaseId,
/// falling back to db0 in ConsumerLoop, CompleteAsync, and DeferAsync.
/// </summary>
public class NonDefaultDatabaseTests
{
    public record NonDefaultDbMessage(string Id);

    public class Handler
    {
        private readonly TaskCompletionSource<string> _tcs;
        public Handler(TaskCompletionSource<string> tcs) => _tcs = tcs;
        public void Handle(NonDefaultDbMessage m) => _tcs.TrySetResult(m.Id);
    }

    [Fact]
    public async Task listener_should_consume_from_non_default_database()
    {
        const int databaseId = 1;
        var streamKey = $"wolverine-tests-db1-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();

                opts.ListenToRedisStream(streamKey, "g1", databaseId)
                    .DefaultIncomingMessage<NonDefaultDbMessage>()
                    .BlockTimeout(100.Milliseconds())
                    .StartFromBeginning();

                opts.PublishAllMessages().ToRedisStream(streamKey, databaseId);

                opts.Services.AddSingleton(tcs);
            })
            .StartAsync();

        var bus = host.MessageBus();
        var uri = new Uri($"redis://stream/{databaseId}/{streamKey}");
        await bus.EndpointFor(uri).SendAsync(new NonDefaultDbMessage("db1-test"));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.ShouldBe(tcs.Task, "Message on non-default database was never consumed — listener likely fell back to db0");

        var result = await tcs.Task;
        result.ShouldBe("db1-test");
    }
}
