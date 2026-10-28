using System.Text.Json;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Pubsub.Http;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
///     The durable inbox deduplicates on <see cref="Envelope.Id" />. A message from a publisher other than Wolverine has
///     no Wolverine <c>id</c> attribute, so the id it is stored under comes from the Pub/Sub message itself: its message
///     id on the subscription that delivered it. Pulled and pushed deliveries derive it the same way.
/// </summary>
public class pubsub_message_identity : IAsyncLifetime
{
    private const string OrdersSubscription = "projects/wolverine/subscriptions/orders";

    private IHost _host = default!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("wolverine").AddTenant("tenant-a", "tenant-project");

                // Messages a third party publishes to "orders" carry no Wolverine attributes at all
                opts.ListenToPubsubTopic("orders").DefaultIncomingMessage<ThirdPartyOrder>();

                opts.StubAllExternalTransports();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private IWolverineRuntime runtime => _host.GetRuntime();

    private PubsubEndpoint orders => runtime.Options.Transports.GetOrCreate<PubsubTransport>().Topics["orders"];

    private IPubsubEnvelopeMapper ordersMapper => orders.BuildMapper(runtime);

    private static PubsubMessage publishedByThirdParty(string messageId)
    {
        return new PubsubMessage
        {
            MessageId = messageId,
            Data = ByteString.CopyFromUtf8("""{"Sku":"T-65"}""")
        };
    }

    /// <summary>
    ///     The envelope a pulling listener on <paramref name="subscription" /> reads the message into
    /// </summary>
    private Envelope pulled(PubsubMessage message, string subscription = OrdersSubscription,
        IPubsubEnvelopeMapper? mapper = null)
    {
        return PubsubMessageIdentity.ReadEnvelope(mapper ?? ordersMapper, message, subscription);
    }

    /// <summary>
    ///     The envelope the push receiver reads the same message into, when a push subscription delivers it
    /// </summary>
    private async Task<Envelope> pushed(PubsubMessage message, string subscription = OrdersSubscription)
    {
        var body = new
        {
            message = new
            {
                attributes = message.Attributes.ToDictionary(x => x.Key, x => x.Value),
                data = message.Data.ToBase64(),
                messageId = message.MessageId
            },
            subscription
        };

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));

        var envelopes = await new PubsubPushEnvelopeMapper(runtime).MapAsync(context, CancellationToken.None);
        return envelopes.Single();
    }

    [Fact]
    public void every_delivery_of_a_third_party_message_has_the_same_id()
    {
        var first = pulled(publishedByThirdParty("3001"));
        var redelivered = pulled(publishedByThirdParty("3001"));

        redelivered.Id.ShouldBe(first.Id);
        first.Id.ShouldBe(PubsubMessageIdentity.IdFor(OrdersSubscription, "3001"));
    }

    [Fact]
    public void another_message_has_another_id()
    {
        pulled(publishedByThirdParty("3002")).Id.ShouldNotBe(pulled(publishedByThirdParty("3001")).Id);
    }

    [Fact]
    public void the_same_message_on_another_subscription_has_another_id()
    {
        // One message is delivered once to every subscription on its topic, and each must handle it once
        var orders = pulled(publishedByThirdParty("3001"));
        var audit = pulled(publishedByThirdParty("3001"), "projects/wolverine/subscriptions/audit");

        audit.Id.ShouldNotBe(orders.Id);
    }

    [Fact]
    public void the_same_message_id_in_a_tenant_project_has_another_id()
    {
        // Message ids are unique per topic, and a project-per-tenant transport has one topic per tenant project
        var defaultProject = pulled(publishedByThirdParty("3001"));
        var tenantProject = pulled(publishedByThirdParty("3001"), "projects/tenant-project/subscriptions/orders");

        tenantProject.Id.ShouldNotBe(defaultProject.Id);
    }

    [Fact]
    public void a_listener_identifies_messages_by_the_subscription_resource_name_a_push_request_carries()
    {
        // A push request names its subscription as projects/{project}/subscriptions/{id}, and so does the pulling
        // listener -- for the default project and for a tenant's project alike
        orders.SubscriptionNameFor("wolverine").ToString().ShouldBe(OrdersSubscription);
        orders.SubscriptionNameFor("tenant-project").ToString()
            .ShouldBe("projects/tenant-project/subscriptions/orders");
    }

    [Fact]
    public async Task a_pulled_and_a_pushed_delivery_of_the_same_message_have_the_same_id()
    {
        var message = publishedByThirdParty("3003");

        var pulledEnvelope = pulled(message);
        var pushedEnvelope = await pushed(message);

        pulledEnvelope.Id.ShouldBe(pushedEnvelope.Id);

        // And for a tenant's project
        var tenantSubscription = "projects/tenant-project/subscriptions/orders";
        pulled(message, tenantSubscription).Id.ShouldBe((await pushed(message, tenantSubscription)).Id);
    }

    [Fact]
    public void a_wolverine_id_attribute_wins()
    {
        var id = Guid.NewGuid();
        var message = publishedByThirdParty("3004");
        message.Attributes[EnvelopeConstants.IdKey] = id.ToString();

        pulled(message).Id.ShouldBe(id);
    }

    [Fact]
    public void an_unreadable_id_attribute_is_not_an_id()
    {
        var message = publishedByThirdParty("3005");
        message.Attributes[EnvelopeConstants.IdKey] = "order-3005";

        pulled(message).Id.ShouldBe(PubsubMessageIdentity.IdFor(OrdersSubscription, "3005"));
    }

    [Fact]
    public void the_id_a_custom_mapper_assigns_wins()
    {
        var mapper = new MapperThatReadsTheOrderId(ordersMapper);
        var orderId = Guid.NewGuid();
        var message = publishedByThirdParty("3006");
        message.Attributes["order-id"] = orderId.ToString();

        pulled(message, mapper: mapper).Id.ShouldBe(orderId);
    }

    [Fact]
    public void a_message_without_a_pubsub_message_id_keeps_the_id_of_its_mapping()
    {
        // Pub/Sub always assigns one, so this is only a message that was never published, but there is nothing
        // stable to derive an id from
        var envelope = pulled(publishedByThirdParty(""));

        envelope.Id.ShouldBe(Guid.Empty);
    }

    /// <summary>
    ///     An interop mapper that identifies a message by its publisher's own order id
    /// </summary>
    public class MapperThatReadsTheOrderId : IPubsubEnvelopeMapper
    {
        private readonly IPubsubEnvelopeMapper _inner;

        public MapperThatReadsTheOrderId(IPubsubEnvelopeMapper inner)
        {
            _inner = inner;
        }

        public void MapEnvelopeToOutgoing(Envelope envelope, PubsubMessage outgoing)
        {
            _inner.MapEnvelopeToOutgoing(envelope, outgoing);
        }

        public void MapIncomingToEnvelope(Envelope envelope, PubsubMessage incoming)
        {
            _inner.MapIncomingToEnvelope(envelope, incoming);

            if (incoming.Attributes.TryGetValue("order-id", out var orderId))
            {
                envelope.Id = Guid.Parse(orderId);
            }
        }

        public void MapOutgoingToMessage(OutgoingMessageBatch outgoing, PubsubMessage message)
        {
            _inner.MapOutgoingToMessage(outgoing, message);
        }
    }
}
