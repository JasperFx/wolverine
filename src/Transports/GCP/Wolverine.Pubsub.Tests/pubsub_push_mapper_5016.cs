using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Transport.Push;
using Wolverine.Pubsub.Http;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
///     GH-5016. A Pub/Sub push subscription delivers to the transport-agnostic push receiver, and the Pub/Sub push
///     mapper reads each pushed message through the Pub/Sub endpoint whose subscription it came from
/// </summary>
public class pubsub_push_mapper_5016 : IAsyncLifetime
{
    private WebApplication _app = default!;
    private HttpClient _client = default!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PushedPubsubHandler));
            opts.Services.AddSingleton<PushedPubsubRecorder>();

            opts.UsePubsub("wolverine").AddTenant("tenant-a", "tenant-project");

            // Messages a third party publishes to "orders" carry no Wolverine headers at all
            opts.ListenToPubsubTopic("orders").DefaultIncomingMessage<ThirdPartyOrder>();

            // Messages another Wolverine service publishes to "events"
            opts.ListenToPubsubTopic("events");

            opts.StubAllExternalTransports();
        });

        _app = builder.Build();

        #region sample_map_pubsub_push_receiver

        // Every push subscription can share one route: each pushed message is read by the Pub/Sub endpoint
        // whose subscription the push request names. Protect this endpoint before you deploy it, for example
        // with the push subscription's OIDC token
        _app.MapPubsubPushReceiver("/pubsub/push");

        // Or read everything pushed to a route through one Pub/Sub endpoint
        _app.MapPubsubPushReceiver("/pubsub/orders", endpointUri: new Uri("pubsub://wolverine/orders"));

        #endregion

        _app.MapWolverinePushReceiver<PubsubPushEnvelopeMapper>("/pubsub/generic");

        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private IWolverineRuntime runtime => _app.Services.GetRequiredService<IWolverineRuntime>();

    private PushedPubsubRecorder recorder => _app.Services.GetRequiredService<PushedPubsubRecorder>();

    private PubsubEndpoint orders => runtime.Options.Transports.GetOrCreate<PubsubTransport>().Topics["orders"];

    private PubsubEndpoint events => runtime.Options.Transports.GetOrCreate<PubsubTransport>().Topics["events"];

    /// <summary>
    ///     The Pub/Sub message Wolverine itself publishes for <paramref name="message" />
    /// </summary>
    private PubsubMessage publishedByWolverine(object message, Guid id)
    {
        var envelope = new Envelope(message)
        {
            Id = id,
            Data = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType()),
            ContentType = "application/json"
        };

        var pubsub = new PubsubMessage { MessageId = "1001" };
        new PubsubEnvelopeMapper(events).MapEnvelopeToOutgoing(envelope, pubsub);
        return pubsub;
    }

    private static PubsubMessage publishedByThirdParty(string json, string messageId)
    {
        return new PubsubMessage { MessageId = messageId, Data = Google.Protobuf.ByteString.CopyFromUtf8(json) };
    }

    /// <summary>
    ///     The body of a wrapped push request, as a push subscription sends it
    /// </summary>
    private static object pushBody(PubsubMessage message, string subscription = "projects/wolverine/subscriptions/orders")
    {
        return new
        {
            message = new
            {
                attributes = message.Attributes.ToDictionary(x => x.Key, x => x.Value),
                data = message.Data.ToBase64(),
                messageId = message.MessageId,
                message_id = message.MessageId,
                publishTime = "2026-10-14T12:00:00.000Z",
                publish_time = "2026-10-14T12:00:00.000Z"
            },
            subscription
        };
    }

    private async Task<Envelope?> map(object body, PubsubPushEnvelopeMapper? mapper = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
        return await (mapper ?? new PubsubPushEnvelopeMapper(runtime)).MapAsync(context, CancellationToken.None);
    }

    private Task<HttpResponseMessage> post(string url, object body) => _client.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);

    [Fact]
    public async Task receives_a_message_wolverine_published()
    {
        var id = Guid.NewGuid();
        var response = await post("/pubsub/push",
            pushBody(publishedByWolverine(new PushedPubsubMessage("Hoth"), id), "projects/wolverine/subscriptions/events"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        recorder.Received.ShouldContain(x => x.Id == id && x.Name == "Hoth");
    }

    [Fact]
    public async Task receives_a_third_party_message_through_the_endpoint_of_its_subscription()
    {
        var response = await post("/pubsub/push", pushBody(publishedByThirdParty("""{"Sku":"T-65"}""", "2001")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        recorder.Received.ShouldContain(x => x.Name == "T-65");
    }

    [Fact]
    public async Task receives_through_an_explicit_endpoint_whatever_the_subscription()
    {
        var response = await post("/pubsub/orders",
            pushBody(publishedByThirdParty("""{"Sku":"Y-Wing"}""", "2002"), "projects/wolverine/subscriptions/unknown"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        recorder.Received.ShouldContain(x => x.Name == "Y-Wing");
    }

    [Fact]
    public async Task the_mapper_can_be_resolved_by_the_generic_push_receiver()
    {
        var response = await post("/pubsub/generic", pushBody(publishedByThirdParty("""{"Sku":"A-Wing"}""", "2003")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        recorder.Received.ShouldContain(x => x.Name == "A-Wing");
    }

    [Fact]
    public async Task a_request_that_is_not_a_push_is_a_bad_request()
    {
        var response = await _client.PostAsync("/pubsub/push", new StringContent("not json", System.Text.Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await post("/pubsub/push", new { subscription = "projects/wolverine/subscriptions/orders" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task keeps_the_wolverine_envelope_headers()
    {
        var id = Guid.NewGuid();
        var envelope = (await map(pushBody(publishedByWolverine(new PushedPubsubMessage("Dagobah"), id),
            "projects/wolverine/subscriptions/events")))!;

        envelope.Id.ShouldBe(id);
        envelope.MessageType.ShouldBe(typeof(PushedPubsubMessage).ToMessageTypeName());
        envelope.ContentType.ShouldBe("application/json");
        envelope.Destination.ShouldBe(events.Uri);
    }

    [Fact]
    public async Task a_third_party_message_gets_a_stable_id_from_its_pubsub_message_id()
    {
        var first = (await map(pushBody(publishedByThirdParty("{}", "3001"))))!;
        var redelivered = (await map(pushBody(publishedByThirdParty("{}", "3001"))))!;
        var another = (await map(pushBody(publishedByThirdParty("{}", "3002"))))!;
        var otherSubscription =
            (await map(pushBody(publishedByThirdParty("{}", "3001"), "projects/wolverine/subscriptions/audit")))!;

        redelivered.Id.ShouldBe(first.Id);
        another.Id.ShouldNotBe(first.Id);
        otherSubscription.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public async Task an_unknown_subscription_is_read_by_the_default_mapping()
    {
        var id = Guid.NewGuid();
        var envelope = (await map(pushBody(publishedByWolverine(new PushedPubsubMessage("Endor"), id),
            "projects/wolverine/subscriptions/unknown")))!;

        envelope.Id.ShouldBe(id);
        envelope.MessageType.ShouldBe(typeof(PushedPubsubMessage).ToMessageTypeName());

        // Left for the push receiver to stamp with its own address
        envelope.Destination.ShouldBeNull();
    }

    [Fact]
    public async Task a_subscription_in_a_tenant_project_stamps_the_tenant()
    {
        var envelope = (await map(pushBody(publishedByThirdParty("{}", "4001"),
            "projects/tenant-project/subscriptions/orders")))!;

        envelope.TenantId.ShouldBe("tenant-a");
        envelope.Destination.ShouldBe(orders.Uri);

        (await map(pushBody(publishedByThirdParty("{}", "4002"))))!.TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task reads_a_batch_of_one()
    {
        var single = new Envelope(new PushedPubsubMessage("Bespin"))
        {
            Data = JsonSerializer.SerializeToUtf8Bytes(new PushedPubsubMessage("Bespin")),
            ContentType = "application/json",
            Destination = orders.Uri
        };

        var message = new PubsubMessage { MessageId = "5001" };
        new PubsubEnvelopeMapper(orders).MapOutgoingToMessage(
            new OutgoingMessageBatch(orders.Uri, [single]), message);

        var envelope = (await map(pushBody(message)))!;
        envelope.Id.ShouldBe(single.Id);
        envelope.MessageType.ShouldBe(typeof(PushedPubsubMessage).ToMessageTypeName());
    }

    [Fact]
    public async Task refuses_a_batch_of_several()
    {
        var envelopes = new[] { "Kessel", "Jakku" }.Select(name => new Envelope(new PushedPubsubMessage(name))
        {
            Data = JsonSerializer.SerializeToUtf8Bytes(new PushedPubsubMessage(name)),
            ContentType = "application/json",
            Destination = orders.Uri
        }).ToList();

        var message = new PubsubMessage { MessageId = "5002" };
        new PubsubEnvelopeMapper(orders).MapOutgoingToMessage(new OutgoingMessageBatch(orders.Uri, envelopes), message);

        await Should.ThrowAsync<NotSupportedException>(async () => await map(pushBody(message)));
    }

    [Fact]
    public async Task an_explicit_endpoint_that_is_not_pubsub_is_refused()
    {
        var mapper = new PubsubPushEnvelopeMapper(runtime) { EndpointUri = new Uri("local://nowhere") };
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await map(pushBody(publishedByThirdParty("{}", "6001")), mapper));
    }
}

public record PushedPubsubMessage(string Name);

public record ThirdPartyOrder(string Sku);

public record ReceivedPubsubPush(Guid Id, string Name);

public class PushedPubsubRecorder
{
    public List<ReceivedPubsubPush> Received { get; } = new();
}

public class PushedPubsubHandler
{
    public static void Handle(PushedPubsubMessage message, Envelope envelope, PushedPubsubRecorder recorder)
    {
        lock (recorder) recorder.Received.Add(new ReceivedPubsubPush(envelope.Id, message.Name));
    }

    public static void Handle(ThirdPartyOrder order, Envelope envelope, PushedPubsubRecorder recorder)
    {
        lock (recorder) recorder.Received.Add(new ReceivedPubsubPush(envelope.Id, order.Sku));
    }
}
