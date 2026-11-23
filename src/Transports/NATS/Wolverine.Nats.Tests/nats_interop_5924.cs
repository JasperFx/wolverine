using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using NSubstitute;
using Shouldly;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-5924. A NATS endpoint takes a pluggable <see cref="INatsEnvelopeMapper" /> with <c>UseInterop(...)</c>,
/// CloudEvents with <c>InteropWithCloudEvents()</c>, and plain JSON with <c>ReceiveRawJson()</c> /
/// <c>PublishRawJson()</c>, for core NATS and JetStream, the way every other broker transport does. The other side
/// of each test is a raw NATS client, not Wolverine.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class nats_interop_5924
{
    // What a non-Wolverine publisher names the message: registered with opts.RegisterMessageType()
    public const string ColorAlias = "nats.interop.color";

    // What Wolverine names the message on the way out
    private static readonly string ColorTypeName = typeof(NatsInteropColor).ToMessageTypeName();

    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public nats_interop_5924(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string newSubject() => $"interop5924.{Guid.NewGuid():N}";

    private static byte[] cloudEvent(string color, string? id = null, string? tenantId = null)
    {
        var json = new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = id ?? Guid.NewGuid().ToString(),
            ["source"] = "not-wolverine",
            ["type"] = ColorAlias,
            ["datacontenttype"] = "application/json",
            ["data"] = new JsonObject { ["color"] = color }
        };

        if (tenantId != null)
        {
            json["tenantid"] = tenantId;
        }

        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    private Task<IHost> startHostAsync(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(NatsInteropColorHandler))
                    .IncludeType(typeof(NatsInteropQuestionHandler));
                opts.RegisterMessageType(typeof(NatsInteropColor), ColorAlias);
                configure(opts);
            }).StartAsync(Ct);
    }

    private async Task<NatsConnection> rawConnectionAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        return connection;
    }

    private static Task<ITrackedSession> receiveAsync(IHost host, Func<Task> publish)
    {
        return host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<NatsInteropColor>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => publish()));
    }

    // ---------------- the default mapping is unchanged ----------------

    [Fact]
    public async Task the_default_mapper_is_the_built_in_one_and_keeps_wolverines_headers()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.PublishMessage<NatsInteropColor>().ToNatsSubject(subject);
        });

        var endpoint = host.GetRuntime().Options.Transports.GetOrCreate<NatsTransport>().EndpointForSubject(subject);
        endpoint.BuildMapper(host.GetRuntime()).ShouldBeOfType<NatsEnvelopeMapper>();
        endpoint.UsesCustomEnvelopeMapper.ShouldBeFalse();

        await using var raw = await NatsTestHelpers.SubscribeRawAsync(_fixture.ConnectionString, subject);
        await host.MessageBus().PublishAsync(new NatsInteropColor("red"));

        var message = await raw.ReadAsync(10.Seconds());
        message.ShouldNotBeNull();
        message.Value.Headers!["message-type"].ToString().ShouldBe(ColorTypeName);
        message.Value.Headers.ContainsKey("id").ShouldBeTrue();
        JsonNode.Parse(message.Value.Data)!["color"]!.GetValue<string>().ShouldBe("red");
    }

    // ---------------- a custom mapper per endpoint ----------------

    [Fact]
    public async Task a_custom_mapper_reads_what_a_raw_nats_publisher_sends()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.ListenToNatsSubject(subject).UseInterop(new KindHeaderNatsMapper());
        });

        await using var connection = await rawConnectionAsync();

        var session = await receiveAsync(host, async () =>
        {
            var headers = new NatsHeaders { [KindHeaderNatsMapper.KindHeader] = ColorAlias, ["x-origin"] = "go" };
            await connection.PublishAsync(subject, """{"color":"green"}"""u8.ToArray(), headers,
                cancellationToken: Ct);
        });

        var envelope = session.Received.SingleEnvelope<NatsInteropColor>();
        envelope.Message.ShouldBe(new NatsInteropColor("green"));
        envelope.Headers["x-origin"].ShouldBe("go");
    }

    [Fact]
    public async Task a_custom_mapper_writes_what_a_raw_nats_subscriber_reads()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.PublishMessage<NatsInteropColor>().ToNatsSubject(subject).UseInterop(new KindHeaderNatsMapper());
        });

        await using var raw = await NatsTestHelpers.SubscribeRawAsync(_fixture.ConnectionString, subject);
        await host.MessageBus().PublishAsync(new NatsInteropColor("blue"));

        var message = await raw.ReadAsync(10.Seconds());
        message.ShouldNotBeNull();
        message.Value.Headers![KindHeaderNatsMapper.KindHeader].ToString().ShouldBe(ColorTypeName);

        // Nothing of the built in mapping
        message.Value.Headers.ContainsKey("message-type").ShouldBeFalse();
        message.Value.Headers.ContainsKey("id").ShouldBeFalse();
    }

    [Fact]
    public async Task the_built_in_mapper_can_be_customized()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.PublishMessage<NatsInteropColor>().ToNatsSubject(subject)
                .UseInterop((_, mapper) => mapper.MapPropertyToHeader(x => x.CorrelationId!, "x-correlation"));
        });

        await using var raw = await NatsTestHelpers.SubscribeRawAsync(_fixture.ConnectionString, subject);
        await host.MessageBus().PublishAsync(new NatsInteropColor("blue"));

        var message = await raw.ReadAsync(10.Seconds());
        message.ShouldNotBeNull();
        message.Value.Headers!.ContainsKey("x-correlation").ShouldBeTrue();
        message.Value.Headers["message-type"].ToString().ShouldBe(ColorTypeName);
    }

    [Fact]
    public async Task a_custom_mapper_on_a_jetstream_listener()
    {
        var subject = newSubject();
        var stream = $"INTEROP5924_{Guid.NewGuid():N}";
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString).AutoProvision().UseJetStream(_ => { })
                .DefineStream(stream, s => s.WithSubjects(subject));
            opts.ListenToNatsSubject(subject).UseJetStream(stream).UseInterop(new KindHeaderNatsMapper());
        });

        await using var connection = await rawConnectionAsync();
        var js = connection.CreateJetStreamContext();

        var session = await receiveAsync(host, async () =>
        {
            var headers = new NatsHeaders { [KindHeaderNatsMapper.KindHeader] = ColorAlias };
            await js.PublishAsync(subject, """{"color":"teal"}"""u8.ToArray(), headers: headers,
                cancellationToken: Ct);
        });

        var envelope = session.Received.SingleEnvelope<NatsInteropColor>();
        envelope.Message.ShouldBe(new NatsInteropColor("teal"));

        // The JetStream metadata is there for a custom mapper too
        envelope.Headers["nats-stream"].ShouldBe(stream);
        envelope.Headers["nats-stream-seq"].ShouldNotBeNull();
    }

    // ---------------- CloudEvents ----------------

    [Fact]
    public async Task cloud_events_from_a_raw_nats_publisher_are_handled()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.ListenToNatsSubject(subject).InteropWithCloudEvents();
        });

        await using var connection = await rawConnectionAsync();
        var id = Guid.NewGuid();

        // No headers at all: everything Wolverine needs is in the CloudEvents document
        var session = await receiveAsync(host,
            async () => await connection.PublishAsync(subject, cloudEvent("purple", id.ToString(), "tenant1"),
                cancellationToken: Ct));

        var envelope = session.Received.SingleEnvelope<NatsInteropColor>();
        envelope.Message.ShouldBe(new NatsInteropColor("purple"));
        envelope.Id.ShouldBe(id);
        envelope.TenantId.ShouldBe("tenant1");
        envelope.Source.ShouldBe("not-wolverine");
    }

    [Fact]
    public async Task cloud_events_published_by_wolverine_reach_a_raw_nats_subscriber()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.PublishMessage<NatsInteropColor>().ToNatsSubject(subject).InteropWithCloudEvents();
        });

        await using var raw = await NatsTestHelpers.SubscribeRawAsync(_fixture.ConnectionString, subject);
        await host.MessageBus().PublishAsync(new NatsInteropColor("orange"), new DeliveryOptions { TenantId = "tenant2" });

        var message = await raw.ReadAsync(10.Seconds());
        message.ShouldNotBeNull();

        var document = JsonNode.Parse(message.Value.Data)!;
        document["specversion"]!.GetValue<string>().ShouldBe("1.0");
        document["type"]!.GetValue<string>().ShouldBe(ColorTypeName);
        document["data"]!["color"]!.GetValue<string>().ShouldBe("orange");
        document["tenantid"]!.GetValue<string>().ShouldBe("tenant2");
    }

    [Fact]
    public async Task cloud_events_from_a_raw_jetstream_publisher_are_handled()
    {
        var subject = newSubject();
        var stream = $"INTEROP5924_{Guid.NewGuid():N}";
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString).AutoProvision().UseJetStream(_ => { })
                .DefineStream(stream, s => s.WithSubjects(subject));
            opts.ListenToNatsSubject(subject).UseJetStream(stream).InteropWithCloudEvents();
        });

        await using var connection = await rawConnectionAsync();
        var js = connection.CreateJetStreamContext();
        var id = Guid.NewGuid();

        var session = await receiveAsync(host,
            async () => await js.PublishAsync(subject, cloudEvent("indigo", id.ToString()), cancellationToken: Ct));

        var envelope = session.Received.SingleEnvelope<NatsInteropColor>();
        envelope.Message.ShouldBe(new NatsInteropColor("indigo"));
        envelope.Id.ShouldBe(id);
        envelope.Headers["nats-stream"].ShouldBe(stream);
    }

    [Fact]
    public async Task cloud_events_published_by_wolverine_over_jetstream_reach_a_raw_consumer()
    {
        var subject = newSubject();
        var stream = $"INTEROP5924_{Guid.NewGuid():N}";
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString).AutoProvision().UseJetStream(_ => { })
                .DefineStream(stream, s => s.WithSubjects(subject));
            opts.PublishMessage<NatsInteropColor>().ToNatsSubject(subject).UseJetStream(stream)
                .InteropWithCloudEvents();
        });

        await host.MessageBus().PublishAsync(new NatsInteropColor("amber"));

        await using var connection = await rawConnectionAsync();
        var js = connection.CreateJetStreamContext();
        var consumer = await js.CreateOrUpdateConsumerAsync(stream,
            new ConsumerConfig { DeliverPolicy = ConsumerConfigDeliverPolicy.All, AckPolicy = ConsumerConfigAckPolicy.None },
            Ct);

        var message = await consumer.NextAsync<byte[]>(opts: new NatsJSNextOpts { Expires = 10.Seconds() },
            cancellationToken: Ct);
        message.ShouldNotBeNull();

        var document = JsonNode.Parse(message.Data)!;
        document["type"]!.GetValue<string>().ShouldBe(ColorTypeName);
        document["data"]!["color"]!.GetValue<string>().ShouldBe("amber");
    }

    [Fact]
    public async Task cloud_events_between_two_wolverine_endpoints_keep_headers_and_tenant()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.PublishMessage<NatsInteropColor>().ToNatsSubject(subject).InteropWithCloudEvents();
            opts.ListenToNatsSubject(subject).InteropWithCloudEvents();
        });

        var options = new DeliveryOptions { TenantId = "tenant3" };
        options.Headers["x-user"] = "alice";

        var session = await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<NatsInteropColor>(host)
            .PublishMessageAndWaitAsync(new NatsInteropColor("yellow"), options);

        var envelope = session.Received.SingleEnvelope<NatsInteropColor>();
        envelope.Message.ShouldBe(new NatsInteropColor("yellow"));
        envelope.TenantId.ShouldBe("tenant3");
        envelope.Headers["x-user"].ShouldBe("alice");
    }

    [Fact]
    public async Task request_reply_through_cloud_events_endpoints()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.PublishMessage<NatsInteropQuestion>().ToNatsSubject(subject).InteropWithCloudEvents();
            opts.ListenToNatsSubject(subject).InteropWithCloudEvents();
        });

        var answer = await host.MessageBus()
            .InvokeAsync<NatsInteropAnswer>(new NatsInteropQuestion(20), Ct, 20.Seconds());

        answer.Value.ShouldBe(40);
    }

    [Fact]
    public async Task scheduled_delivery_through_cloud_events_endpoints()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.PublishMessage<NatsInteropColor>().ToNatsSubject(subject).InteropWithCloudEvents();
            opts.ListenToNatsSubject(subject).InteropWithCloudEvents();
        });

        var session = await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<NatsInteropColor>(host)
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(new NatsInteropColor("later"), 1.Seconds()).AsTask());

        session.Received.SingleMessage<NatsInteropColor>().Color.ShouldBe("later");
    }

    // ---------------- raw JSON ----------------

    [Fact]
    public async Task raw_json_from_a_raw_nats_publisher_is_handled()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.ListenToNatsSubject(subject).ReceiveRawJson<NatsInteropColor>();
        });

        host.GetRuntime().Options.Transports.GetOrCreate<NatsTransport>().EndpointForSubject(subject)
            .BuildMapper(host.GetRuntime()).ShouldBeOfType<JsonOnlyMapper>();

        await using var connection = await rawConnectionAsync();

        var session = await receiveAsync(host,
            async () => await connection.PublishAsync(subject, """{"color":"black"}"""u8.ToArray(),
                cancellationToken: Ct));

        session.Received.SingleMessage<NatsInteropColor>().Color.ShouldBe("black");
    }

    [Fact]
    public async Task raw_json_from_a_raw_jetstream_publisher_is_handled()
    {
        var subject = newSubject();
        var stream = $"INTEROP5924_{Guid.NewGuid():N}";
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString).AutoProvision().UseJetStream(_ => { })
                .DefineStream(stream, s => s.WithSubjects(subject));
            opts.ListenToNatsSubject(subject).UseJetStream(stream).ReceiveRawJson<NatsInteropColor>();
        });

        await using var connection = await rawConnectionAsync();
        var js = connection.CreateJetStreamContext();

        var session = await receiveAsync(host,
            async () => await js.PublishAsync(subject, """{"color":"white"}"""u8.ToArray(), cancellationToken: Ct));

        session.Received.SingleMessage<NatsInteropColor>().Color.ShouldBe("white");
    }

    [Fact]
    public async Task publish_raw_json_writes_no_wolverine_headers()
    {
        var subject = newSubject();
        using var host = await startHostAsync(opts =>
        {
            opts.UseNats(_fixture.ConnectionString);
            opts.PublishMessage<NatsInteropColor>().ToNatsSubject(subject)
                .PublishRawJson()
                .AddOutgoingHeader("x-app", "orders");
        });

        await using var raw = await NatsTestHelpers.SubscribeRawAsync(_fixture.ConnectionString, subject);
        await host.MessageBus().PublishAsync(new NatsInteropColor("gray"));

        var message = await raw.ReadAsync(10.Seconds());
        message.ShouldNotBeNull();
        JsonNode.Parse(message.Value.Data)!["color"]!.GetValue<string>().ShouldBe("gray");

        // Only the header the application asked for
        message.Value.Headers.ShouldNotBeNull();
        message.Value.Headers.Keys.ShouldBe(["x-app"]);
    }

    // ---------------- no broker ----------------

    [Fact]
    public void a_jetstream_message_is_mapped_by_the_endpoints_own_mapper()
    {
        var endpoint = new NatsTransport().EndpointForSubject("orders.>");
        var mapper = Substitute.For<INatsEnvelopeMapper>();
        endpoint.EnvelopeMapper = mapper;

        var message = Substitute.For<INatsJSMsg<byte[]>>();
        message.Subject.Returns("orders.1");
        message.ReplyTo.Returns("reply.1");
        message.Data.Returns([1, 2]);
        message.Headers.Returns(new NatsHeaders { ["x-kind"] = "color" });

        var envelope = new NatsIncomingMapping(endpoint, mapper, null).TryMap(message, null);

        // A custom mapper decides what it can read, so the envelope is handed on even without a message type
        envelope.ShouldNotBeNull();
        mapper.Received(1).MapIncomingToEnvelope(envelope,
            Arg.Is<NatsMsg<byte[]>>(m => m.Subject == "orders.1" && m.ReplyTo == "reply.1" &&
                                         m.Headers!["x-kind"] == "color" && m.Data!.Length == 2));
    }

    [Fact]
    public void the_built_in_mapping_still_skips_a_message_without_a_message_type()
    {
        var endpoint = new NatsTransport().EndpointForSubject("orders");
        var mapping = new NatsIncomingMapping(endpoint, new NatsEnvelopeMapper(endpoint), null);

        mapping.TryMap(new NatsMsg<byte[]>("orders", null, 0, new NatsHeaders { ["x-other"] = "1" }, [1], null))
            .ShouldBeNull();

        mapping.TryMap(new NatsMsg<byte[]>("orders", null, 0, new NatsHeaders { ["message-type"] = "ping" }, [1],
            null)).ShouldNotBeNull();
    }

    [Fact]
    public void the_listener_and_subscriber_configurations_are_interoperable()
    {
        typeof(Configuration.NatsListenerConfiguration).IsSubclassOf(
                typeof(Wolverine.Configuration.InteroperableListenerConfiguration<Configuration.NatsListenerConfiguration,
                    NatsEndpoint, INatsEnvelopeMapper, NatsEnvelopeMapper>))
            .ShouldBeTrue();

        typeof(Configuration.NatsSubscriberConfiguration).IsSubclassOf(
                typeof(Wolverine.Configuration.InteroperableSubscriberConfiguration<
                    Configuration.NatsSubscriberConfiguration, NatsEndpoint, INatsEnvelopeMapper, NatsEnvelopeMapper>))
            .ShouldBeTrue();
    }
}

public record NatsInteropColor(string Color);

public static class NatsInteropColorHandler
{
    public static void Handle(NatsInteropColor message)
    {
    }
}

public record NatsInteropQuestion(int Value);

public record NatsInteropAnswer(int Value);

public static class NatsInteropQuestionHandler
{
    public static NatsInteropAnswer Handle(NatsInteropQuestion question) => new(question.Value * 2);
}
