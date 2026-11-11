using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5452. Every resource discovery, IHost.ResetResourceState() among them, built another
/// wolverine.response.&lt;guid&gt; queue that nobody listens to. Each later reset declared and purged all of them,
/// and the transport's reply endpoint moved to one of the new queues, so a sender started after a reset sent its
/// replies there
/// </summary>
public class resetting_resources_keeps_one_reply_queue_5452 : IAsyncLifetime
{
    private readonly string _queueName = $"gh5452-{Guid.NewGuid():N}";
    private IHost _host = null!;

    private static CancellationToken token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<Ping5452Handler>();
                opts.UseRabbitMq().AutoProvision();
                opts.ListenToRabbitQueue(_queueName);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task resetting_resources_adds_no_reply_queues()
    {
        var transport = _host.GetRuntime().Options.RabbitMqTransport();
        var replyEndpoint = transport.ReplyEndpoint();

        for (var i = 0; i < 3; i++)
        {
            await _host.ResetResourceState(token);
        }

        transport.Queues.Count(x => x.QueueName.StartsWith("wolverine.response.")).ShouldBe(1);
        transport.ReplyEndpoint().ShouldBeSameAs(replyEndpoint);
    }

    [Fact]
    public async Task request_reply_works_through_a_sender_started_after_a_reset()
    {
        await _host.ResetResourceState(token);

        // A listener-only queue has no sending agent yet, so this one is started now, after the reset
        var pong = await _host.MessageBus()
            .EndpointFor(new Uri($"rabbitmq://queue/{_queueName}"))
            .InvokeAsync<Pong5452>(new Ping5452("hello"), token, 10.Seconds());

        pong.Message.ShouldBe("hello");
    }
}

public record Ping5452(string Message);

public record Pong5452(string Message);

public class Ping5452Handler
{
    public static Pong5452 Handle(Ping5452 ping) => new(ping.Message);
}
