using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Xunit;

namespace Wolverine.Redis.Tests;

public class BasicPubSubTests
{
    public record PubMessage(string Id);

    public class Handler
    {
        private readonly TaskCompletionSource<bool> _tcs;
        public Handler(TaskCompletionSource<bool> tcs) => _tcs = tcs;
        public void Handle(PubMessage m) => _tcs.TrySetResult(true);
    }

    [Fact]
    public async Task publish_and_listen_end_to_end()
    {
        var streamKey = $"wolverine-tests-pubsub-{Guid.NewGuid():N}";
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
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                var endpoint = opts.ListenToRedisStream(streamKey, "g1");
                endpoint.MessageType = typeof(PubMessage);
                endpoint.BlockTimeoutMilliseconds = 100;
                endpoint.EnableAutoClaim(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(0));

                opts.PublishAllMessages().ToRedisStream(streamKey);
                opts.Services.AddSingleton(tcs);
            })
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        // Send directly to the Redis stream endpoint to avoid route misconfiguration
        var uri = new Uri($"redis://stream/0/{streamKey}");
        await bus.EndpointFor(uri).SendAsync(new PubMessage("123"));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.ShouldBe(tcs.Task);
        var result = await tcs.Task;
        result.ShouldBeTrue();
    }
}

