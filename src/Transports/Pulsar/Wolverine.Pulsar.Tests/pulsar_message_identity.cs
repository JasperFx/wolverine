using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Pulsar.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// The id a durable Pulsar listener gives a message that has none of its own. No broker required; the end to
/// end behavior is in <see cref="redelivered_message_without_id_property_is_handled_once" />.
/// </summary>
public class pulsar_message_identity
{
    private const string Topic = "persistent://public/default/orders";

    private static readonly PulsarMessageIdentity theIdentity = new("pulsar", null, "orders-subscription");

    [Fact]
    public void name_based_ids_follow_rfc_9562()
    {
        // RFC 9562, appendix A.4: version 5 of "www.example.com" in the DNS namespace
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        PulsarMessageIdentity.CreateVersion5(dns, "www.example.com"u8)
            .ShouldBe(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"));
    }

    [Fact]
    public void a_message_gets_a_version_5_uuid()
    {
        var id = theIdentity.IdFor(new MessageId(10, 42, -1, -1), Topic);

        id.Version.ShouldBe(5);
        (id.Variant & 0b1100).ShouldBe(0b1000);
    }

    [Fact]
    public void the_same_message_always_gets_the_same_id()
    {
        // A different listener instance, as after a restart, a failover to another node or another process
        var afterRestart = new PulsarMessageIdentity("pulsar", null, "orders-subscription");

        afterRestart.IdFor(new MessageId(10, 42, 3, 5), Topic)
            .ShouldBe(theIdentity.IdFor(new MessageId(10, 42, 3, 5), Topic));
    }

    [Fact]
    public void every_coordinate_is_part_of_the_id()
    {
        var ids = new[]
        {
            theIdentity.IdFor(new MessageId(10, 42, 0, 0), Topic),
            theIdentity.IdFor(new MessageId(11, 42, 0, 0), Topic),
            theIdentity.IdFor(new MessageId(10, 43, 0, 0), Topic),
            theIdentity.IdFor(new MessageId(10, 42, 1, 0), Topic),
            theIdentity.IdFor(new MessageId(10, 42, 0, 1), Topic),
            theIdentity.IdFor(new MessageId(10, 42, 0, 0), "persistent://public/default/invoices"),
            new PulsarMessageIdentity("pulsar", null, "billing-subscription").IdFor(new MessageId(10, 42, 0, 0), Topic),
            new PulsarMessageIdentity("pulsar", "tenant-a", "orders-subscription").IdFor(new MessageId(10, 42, 0, 0), Topic),
            new PulsarMessageIdentity("pulsar", "tenant-b", "orders-subscription").IdFor(new MessageId(10, 42, 0, 0), Topic),
            new PulsarMessageIdentity("americas", null, "orders-subscription").IdFor(new MessageId(10, 42, 0, 0), Topic)
        };

        ids.Distinct().Count().ShouldBe(ids.Length);
    }

    [Fact]
    public void the_topic_the_message_id_carries_wins_over_the_consumers()
    {
        // A multi-topic or pattern consumer reports the topic each message came from in its MessageId
        var fromInvoices = new MessageId(10, 42, -1, -1, "persistent://public/default/invoices");

        theIdentity.IdFor(fromInvoices, Topic)
            .ShouldBe(theIdentity.IdFor(new MessageId(10, 42, -1, -1), "persistent://public/default/invoices"));
    }

    [Fact]
    public void components_do_not_run_into_each_other()
    {
        new PulsarMessageIdentity("pulsar", null, "ab").IdFor(new MessageId(1, 1, -1, -1), "c")
            .ShouldNotBe(new PulsarMessageIdentity("pulsar", null, "a").IdFor(new MessageId(1, 1, -1, -1), "bc"));

        new PulsarMessageIdentity("pulsar", "t", "s").IdFor(new MessageId(1, 1, -1, -1), Topic)
            .ShouldNotBe(new PulsarMessageIdentity("pulsar", null, "ts").IdFor(new MessageId(1, 1, -1, -1), Topic));
    }

    [Fact]
    public void a_name_too_long_for_the_stack_is_still_stable()
    {
        var subscription = new string('s', 2000);

        new PulsarMessageIdentity("pulsar", null, subscription).IdFor(new MessageId(1, 7, -1, -1), Topic)
            .ShouldBe(new PulsarMessageIdentity("pulsar", null, subscription).IdFor(new MessageId(1, 7, -1, -1), Topic));

        new PulsarMessageIdentity("pulsar", null, subscription).IdFor(new MessageId(1, 7, -1, -1), Topic)
            .ShouldNotBe(new PulsarMessageIdentity("pulsar", null, subscription + "s")
                .IdFor(new MessageId(1, 7, -1, -1), Topic));
    }

    [Fact]
    public void a_message_without_an_id_property_gets_the_id_of_its_message_id()
    {
        var mapper = defaultMapper();

        var first = PulsarMessageIdentity.CreateEnvelope(mapper, message(entryId: 7), Topic, false, theIdentity);
        var redelivered = PulsarMessageIdentity.CreateEnvelope(mapper, message(entryId: 7), Topic, false, theIdentity);

        first.Id.ShouldBe(theIdentity.IdFor(new MessageId(10, 7, -1, -1), Topic));
        redelivered.Id.ShouldBe(first.Id);
    }

    [Fact]
    public void the_wolverine_id_property_wins()
    {
        var id = Guid.NewGuid();

        var envelope = PulsarMessageIdentity.CreateEnvelope(defaultMapper(),
            message(entryId: 7, idProperty: id.ToString()), Topic, false, theIdentity);

        envelope.Id.ShouldBe(id);
    }

    [Fact]
    public void an_unreadable_id_property_gets_the_id_of_the_message_id()
    {
        // PulsarEnvelopeMapper reads it as Guid.Empty, which is no id at all
        var envelope = PulsarMessageIdentity.CreateEnvelope(defaultMapper(),
            message(entryId: 7, idProperty: "order-1"), Topic, false, theIdentity);

        envelope.Id.ShouldBe(theIdentity.IdFor(new MessageId(10, 7, -1, -1), Topic));
    }

    [Fact]
    public void an_id_a_custom_mapper_assigns_wins()
    {
        var id = Guid.NewGuid();
        var mapper = Substitute.For<IPulsarEnvelopeMapper>();
        mapper.When(x => x.MapIncomingToEnvelope(Arg.Any<Envelope>(), Arg.Any<IMessage<ReadOnlySequence<byte>>>()))
            .Do(ci => ci.Arg<Envelope>().Id = id);

        PulsarMessageIdentity.CreateEnvelope(mapper, message(entryId: 7), Topic, false, theIdentity)
            .Id.ShouldBe(id);
    }

    [Fact]
    public void a_custom_mapper_that_assigns_no_id_gets_the_id_of_the_message_id()
    {
        var mapper = Substitute.For<IPulsarEnvelopeMapper>();

        PulsarMessageIdentity.CreateEnvelope(mapper, message(entryId: 7), Topic, false, theIdentity)
            .Id.ShouldBe(theIdentity.IdFor(new MessageId(10, 7, -1, -1), Topic));
    }

    [Fact]
    public void without_an_identity_nothing_is_derived()
    {
        // Buffered, Inline and NativeAck listeners have no inbox to deduplicate against, so they derive nothing.
        // The id stays as PulsarEnvelopeMapper left it, and the receiver gives the envelope a fresh one
        var envelope = PulsarMessageIdentity.CreateEnvelope(defaultMapper(), message(entryId: 7), Topic, false, null);

        envelope.Id.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void the_envelope_carries_the_message_and_where_it_came_from()
    {
        var incoming = message(entryId: 7);

        var envelope = PulsarMessageIdentity.CreateEnvelope(defaultMapper(), incoming, Topic, true, theIdentity);

        envelope.MessageData.ShouldBeSameAs(incoming);
        envelope.IsFromRetryConsumer.ShouldBeTrue();
        envelope.Data.ShouldBe("{}"u8.ToArray());
    }

    private static PulsarEnvelopeMapper defaultMapper()
    {
        var transport = new PulsarTransport();
        var endpoint = transport.EndpointFor(Topic);
        return new PulsarEnvelopeMapper(endpoint, Substitute.For<IWolverineRuntime>());
    }

    // A message from a producer that is not Wolverine: its properties are someone else's
    private static IMessage<ReadOnlySequence<byte>> message(ulong entryId, string? idProperty = null)
    {
        var properties = new Dictionary<string, string>
        {
            ["ce_id"] = "order-1",
            ["content-type"] = "application/json"
        };

        if (idProperty != null)
        {
            properties[EnvelopeConstants.IdKey] = idProperty;
        }

        var message = Substitute.For<IMessage<ReadOnlySequence<byte>>>();
        message.MessageId.Returns(new MessageId(10, entryId, -1, -1));
        message.Properties.Returns(properties);
        message.Data.Returns(new ReadOnlySequence<byte>("{}"u8.ToArray()));
        return message;
    }
}
