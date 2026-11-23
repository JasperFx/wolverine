using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-5889. With claim check auto-offload, the claim-check serializer stamps the token header while it
/// serializes, which happens on the first read of <see cref="Envelope.Data" />. RabbitMqSender mapped the
/// envelope's headers onto the broker message before it read the body, so a large message reached the broker
/// with an empty body and no token, and the receiver failed with "The input does not contain any JSON tokens".
/// </summary>
public class claim_check_over_rabbitmq_5889 : IAsyncLifetime
{
    private const int Threshold = 1024;
    private const string BodyHeader = "claim-check.$body";

    private readonly string _queueName = $"gh5889-{Guid.NewGuid():N}";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"gh5889-{Guid.NewGuid():N}");
    private IHost _sender = null!;
    private IHost _receiver = null!;

    private static CancellationToken token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<LargeOneWay5889Handler>()
                    .IncludeType<LargeRequest5889Handler>();

                opts.UseClaimCheck(cc =>
                {
                    cc.UseFileSystem(_directory);
                    cc.AutoOffloadPayloadsLargerThan(Threshold);
                });

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.ListenToRabbitQueue(_queueName);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();

                opts.UseClaimCheck(cc =>
                {
                    cc.UseFileSystem(_directory);
                    cc.AutoOffloadPayloadsLargerThan(Threshold);
                });

                opts.UseRabbitMq().AutoProvision();
                opts.PublishMessage<LargeOneWay5889>().ToRabbitQueue(_queueName);
                opts.PublishMessage<LargeRequest5889>().ToRabbitQueue(_queueName);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
        _sender.Dispose();
        _receiver.Dispose();

        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort
        }
    }

    [Fact]
    public async Task an_auto_offloaded_message_round_trips()
    {
        var payload = new string('x', 64 * 1024);

        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new LargeOneWay5889("big", payload));

        session.Sent.SingleEnvelope<LargeOneWay5889>().Headers.Keys.ShouldContain(BodyHeader);

        var received = session.Received.SingleMessage<LargeOneWay5889>();
        received.Name.ShouldBe("big");
        received.Payload.ShouldBe(payload);
    }

    [Fact]
    public async Task an_auto_offloaded_reply_to_invoke_round_trips()
    {
        // The reporter's case: the handler's response is over the threshold and goes back on the reply queue
        var reply = await _sender.MessageBus()
            .InvokeAsync<LargeReply5889>(new LargeRequest5889("hello", 64 * 1024), token, 30.Seconds());

        reply.Name.ShouldBe("hello");
        reply.Payload.Length.ShouldBe(64 * 1024);
    }

    [Fact]
    public void the_rabbit_mapper_carries_the_token_onto_the_broker_properties()
    {
        // Deterministic, no broker: the same mapping RabbitMqSender.SendAsync does before it reads the body
        var serializer = _sender.GetRuntime().Options.DefaultSerializer;
        var queue = new RabbitMqTransport().Queues[_queueName];
        var mapper = new RabbitMqEnvelopeMapper(queue, null!);

        var envelope = new Envelope(new LargeOneWay5889("big", new string('x', 4096)))
        {
            Serializer = serializer,
            ContentType = serializer.ContentType,
            Destination = queue.Uri
        };

        var props = new BasicProperties { Headers = new Dictionary<string, object?>() };
        mapper.MapEnvelopeToOutgoing(envelope, props);

        props.Headers.Keys.ShouldContain(BodyHeader);
        props.Headers[BodyHeader].ShouldBe(envelope.Headers[BodyHeader]);
        envelope.Data.ShouldBeEmpty();
    }
}

public record LargeOneWay5889(string Name, string Payload);

public record LargeRequest5889(string Name, int Size);

public record LargeReply5889(string Name, string Payload);

public class LargeOneWay5889Handler
{
    public static void Handle(LargeOneWay5889 message)
    {
        // Nothing to do, the tracked session asserts on what was received
    }
}

public class LargeRequest5889Handler
{
    public static LargeReply5889 Handle(LargeRequest5889 request) => new(request.Name, new string('y', request.Size));
}
