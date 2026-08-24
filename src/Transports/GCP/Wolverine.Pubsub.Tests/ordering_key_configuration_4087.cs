using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
///     GH-4087. <see cref="PubsubTopicOptions.OrderBy" /> is consulted on every publish but used to have no
///     configuration surface at all. These cover the new <c>OrderMessagesBy()</c> fluent method and, more
///     importantly, the three step precedence the publish path actually implements:
///     <c>GroupId</c> -> <c>OrderBy</c> -> whatever the envelope mapper stamped on the message.
/// </summary>
public class ordering_key_configuration_4087
{
    private static PubsubTransport createTransport()
    {
        return new PubsubTransport
        {
            ProjectId = "wolverine",
            PublisherApiClient = Substitute.For<PublisherServiceApiClient>(),
            SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>(),
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        };
    }

    /// <summary>
    ///     Publishes the envelope through the real <see cref="PubsubEndpoint.SendMessageAsync" /> path against a
    ///     stubbed publisher client, and hands back the <see cref="PubsubMessage" /> that would have gone to GCP.
    /// </summary>
    private static async Task<PubsubMessage> publishAndCapture(PubsubEndpoint endpoint, Envelope envelope)
    {
        var publisher = Substitute.For<PublisherServiceApiClient>();

        publisher
            .PublishAsync(Arg.Any<PublishRequest>())
            .ReturnsForAnyArgs(Task.FromResult(new PublishResponse()));

        var clients = new PubsubClientSet
        {
            ProjectId = "wolverine",
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
            PublisherApiClient = publisher
        };

        await endpoint.SendMessageAsync(envelope, NullLogger.Instance, clients);

        var call = publisher
            .ReceivedCalls()
            .Single(x => x.GetMethodInfo().Name == nameof(PublisherServiceApiClient.PublishAsync));

        var request = call.GetArguments().OfType<PublishRequest>().Single();

        return request.Messages.Single();
    }

    [Fact]
    public void order_by_defaults_to_returning_null()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());

        endpoint.Server.Topic.OrderBy(ObjectMother.Envelope()).ShouldBeNull();
    }

    [Fact]
    public void order_messages_by_reaches_the_topic_options()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());
        var configuration = new PubsubTopicSubscriberConfiguration(endpoint);

        configuration.OrderMessagesBy(e => e.TenantId);

        // delayed configuration -- nothing is assigned until the endpoint is compiled
        endpoint.Server.Topic.OrderBy(new Envelope { TenantId = "tenant1" }).ShouldBeNull();

        ((IDelayedEndpointConfiguration)configuration).Apply();

        endpoint.Server.Topic.OrderBy(new Envelope { TenantId = "tenant1" }).ShouldBe("tenant1");
    }

    [Fact]
    public void order_messages_by_rejects_a_null_function()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());
        var configuration = new PubsubTopicSubscriberConfiguration(endpoint);

        Should.Throw<ArgumentNullException>(() => configuration.OrderMessagesBy(null!));
    }

    [Fact]
    public void order_messages_by_is_fluent()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());
        var configuration = new PubsubTopicSubscriberConfiguration(endpoint);

        configuration.OrderMessagesBy(e => e.TenantId).ShouldBeSameAs(configuration);
    }

    [Fact]
    public async Task order_by_supplies_the_ordering_key_when_there_is_no_group_id()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());
        endpoint.Server.Topic.OrderBy = e => "from-order-by";

        var envelope = ObjectMother.Envelope();
        envelope.GroupId = null;

        var message = await publishAndCapture(endpoint, envelope);

        message.OrderingKey.ShouldBe("from-order-by");
    }

    [Fact]
    public async Task group_id_beats_order_by()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());
        endpoint.Server.Topic.OrderBy = e => "from-order-by";

        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "from-group-id";

        var message = await publishAndCapture(endpoint, envelope);

        message.OrderingKey.ShouldBe("from-group-id");
    }

    [Fact]
    public async Task order_by_beats_the_envelope_mapper()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());
        endpoint.EnvelopeMapper = new OrderingKeyStampingMapper("from-mapper");
        endpoint.Server.Topic.OrderBy = e => "from-order-by";

        var envelope = ObjectMother.Envelope();
        envelope.GroupId = null;

        var message = await publishAndCapture(endpoint, envelope);

        message.OrderingKey.ShouldBe("from-order-by");
    }

    [Fact]
    public async Task the_envelope_mapper_is_the_last_resort()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());
        endpoint.EnvelopeMapper = new OrderingKeyStampingMapper("from-mapper");

        // no GroupId, and OrderBy left at its default of e => null
        var envelope = ObjectMother.Envelope();
        envelope.GroupId = null;

        var message = await publishAndCapture(endpoint, envelope);

        message.OrderingKey.ShouldBe("from-mapper");
    }

    [Fact]
    public async Task group_id_beats_the_envelope_mapper_too()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());
        endpoint.EnvelopeMapper = new OrderingKeyStampingMapper("from-mapper");

        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "from-group-id";

        var message = await publishAndCapture(endpoint, envelope);

        message.OrderingKey.ShouldBe("from-group-id");
    }

    [Fact]
    public async Task no_ordering_key_at_all_by_default()
    {
        var endpoint = new PubsubEndpoint("foo", createTransport());

        var envelope = ObjectMother.Envelope();
        envelope.GroupId = null;

        var message = await publishAndCapture(endpoint, envelope);

        // protobuf string fields are never null, so "unset" is the empty string here
        message.OrderingKey.ShouldBeEmpty();
    }

    /// <summary>
    ///     Stands in for a user supplied <see cref="IPubsubEnvelopeMapper" /> that sets the ordering key itself --
    ///     the lowest rung of the precedence chain.
    /// </summary>
    internal class OrderingKeyStampingMapper : IPubsubEnvelopeMapper
    {
        private readonly string _orderingKey;

        public OrderingKeyStampingMapper(string orderingKey)
        {
            _orderingKey = orderingKey;
        }

        public void MapEnvelopeToOutgoing(Envelope envelope, PubsubMessage outgoing)
        {
            outgoing.OrderingKey = _orderingKey;
        }

        public void MapIncomingToEnvelope(Envelope envelope, PubsubMessage incoming)
        {
        }

        public void MapOutgoingToMessage(OutgoingMessageBatch outgoing, PubsubMessage message)
        {
        }
    }
}
