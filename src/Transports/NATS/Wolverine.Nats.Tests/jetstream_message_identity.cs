using NATS.Client.Core;
using NATS.Client.JetStream;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// The id a durable JetStream listener gives a message that has none of its own. No broker required; the end to end
/// behavior is in <see cref="redelivered_jetstream_message_without_id_header_is_handled_once" />.
/// </summary>
public class jetstream_message_identity
{
    private static readonly DateTimeOffset theStreamCreated = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static readonly JetStreamMessageIdentity theIdentity =
        new("nats", null, "ORDERS", theStreamCreated, "order-processor");

    [Fact]
    public void name_based_ids_follow_rfc_9562()
    {
        // RFC 9562, appendix A.4: version 5 of "www.example.com" in the DNS namespace
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        JetStreamMessageIdentity.CreateVersion5(dns, "www.example.com"u8)
            .ShouldBe(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"));
    }

    [Fact]
    public void a_message_gets_a_version_5_uuid()
    {
        var id = theIdentity.IdFor(42);

        id.Version.ShouldBe(5);
        (id.Variant & 0b1100).ShouldBe(0b1000);
    }

    [Fact]
    public void the_same_message_always_gets_the_same_id()
    {
        // A different listener instance, as after a restart or on another node pulling from the same consumer
        var afterRestart = new JetStreamMessageIdentity("nats", null, "ORDERS", theStreamCreated, "order-processor");

        afterRestart.IdFor(1234).ShouldBe(theIdentity.IdFor(1234));
    }

    [Fact]
    public void every_component_is_part_of_the_id()
    {
        var ids = new[]
        {
            theIdentity.IdFor(7),

            // Another message in the same stream
            theIdentity.IdFor(8),

            // The same sequence in another stream
            new JetStreamMessageIdentity("nats", null, "INVOICES", theStreamCreated, "order-processor").IdFor(7),

            // Another consumer of the same stream is an independent subscription
            new JetStreamMessageIdentity("nats", null, "ORDERS", theStreamCreated, "order-auditor").IdFor(7),

            // A stream deleted and recreated under the same name numbers its messages from 1 again
            new JetStreamMessageIdentity("nats", null, "ORDERS", theStreamCreated.AddDays(1), "order-processor")
                .IdFor(7),
            new JetStreamMessageIdentity("nats", null, "ORDERS", null, "order-processor").IdFor(7),

            // Tenants with their own connection and additional brokers are other servers with their own streams
            new JetStreamMessageIdentity("nats", "tenant-a", "ORDERS", theStreamCreated, "order-processor").IdFor(7),
            new JetStreamMessageIdentity("nats", "tenant-b", "ORDERS", theStreamCreated, "order-processor").IdFor(7),
            new JetStreamMessageIdentity("americas", null, "ORDERS", theStreamCreated, "order-processor").IdFor(7)
        };

        ids.Distinct().Count().ShouldBe(ids.Length);
    }

    [Fact]
    public void components_do_not_run_into_each_other()
    {
        new JetStreamMessageIdentity("nats", null, "AB", theStreamCreated, "c").IdFor(1)
            .ShouldNotBe(new JetStreamMessageIdentity("nats", null, "A", theStreamCreated, "Bc").IdFor(1));

        new JetStreamMessageIdentity("nats", "t", "ORDERS", theStreamCreated, "c").IdFor(1)
            .ShouldNotBe(new JetStreamMessageIdentity("natst", null, "ORDERS", theStreamCreated, "c").IdFor(1));
    }

    [Fact]
    public void a_name_too_long_for_the_stack_is_still_stable()
    {
        var consumer = new string('c', 2000);

        new JetStreamMessageIdentity("nats", null, "ORDERS", theStreamCreated, consumer).IdFor(7)
            .ShouldBe(new JetStreamMessageIdentity("nats", null, "ORDERS", theStreamCreated, consumer).IdFor(7));

        new JetStreamMessageIdentity("nats", null, "ORDERS", theStreamCreated, consumer).IdFor(7)
            .ShouldNotBe(new JetStreamMessageIdentity("nats", null, "ORDERS", theStreamCreated, consumer + "c")
                .IdFor(7));
    }

    [Fact]
    public void a_message_without_an_id_header_gets_the_id_of_its_place_in_the_stream()
    {
        var mapper = theMapper();

        var first = mapper.CreateEnvelope(message(streamSequence: 7, deliveries: 1), theIdentity);
        var redelivered = mapper.CreateEnvelope(message(streamSequence: 7, deliveries: 2), theIdentity);

        first.Id.ShouldBe(theIdentity.IdFor(7));
        redelivered.Id.ShouldBe(first.Id);
    }

    [Fact]
    public void the_wolverine_id_header_wins()
    {
        var id = Guid.NewGuid();

        var envelope = theMapper().CreateEnvelope(message(streamSequence: 7, idHeader: id.ToString()), theIdentity);

        envelope.Id.ShouldBe(id);
    }

    [Fact]
    public void an_unreadable_id_header_gets_the_id_of_its_place_in_the_stream()
    {
        // The mapper reads it as Guid.Empty, which is no id at all
        var envelope = theMapper().CreateEnvelope(message(streamSequence: 7, idHeader: "order-1"), theIdentity);

        envelope.Id.ShouldBe(theIdentity.IdFor(7));
    }

    [Fact]
    public void an_id_from_a_custom_mapping_wins()
    {
        var mapper = theMapper();
        var custom = Guid.NewGuid();
        mapper.MapIncomingProperty(x => x.Id, (envelope, _) => envelope.Id = custom);

        var envelope = mapper.CreateEnvelope(message(streamSequence: 7), theIdentity);

        envelope.Id.ShouldBe(custom);
    }

    [Fact]
    public void without_an_identity_the_message_keeps_the_id_it_was_mapped_with()
    {
        var envelope = theMapper().CreateEnvelope(message(streamSequence: 7), null);

        // No id header reads as Guid.Empty, which the receiver replaces with a fresh id of its own, as before
        envelope.Id.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void the_nats_msg_id_header_is_not_mistaken_for_a_wolverine_id()
    {
        // Wolverine stamps Nats-Msg-Id for the stream's own duplicate window; the inbox id comes from elsewhere
        var envelope = theMapper().CreateEnvelope(
            message(streamSequence: 7, extraHeaders: new() { ["Nats-Msg-Id"] = Guid.NewGuid().ToString() }),
            theIdentity);

        envelope.Id.ShouldBe(theIdentity.IdFor(7));
        envelope.Headers["Nats-Msg-Id"].ShouldNotBeNull();
    }

    [Theory]
    [InlineData(EndpointMode.Durable, true)]
    [InlineData(EndpointMode.BufferedInMemory, false)]
    [InlineData(EndpointMode.Inline, false)]
    [InlineData(EndpointMode.NativeAck, false)]
    public void only_a_durable_listener_derives_ids(EndpointMode mode, bool derives)
    {
        var endpoint = new NatsTransport().EndpointForSubject("orders");
        endpoint.UseJetStream = true;
        endpoint.StreamName = "ORDERS";
        endpoint.Mode = mode;

        JetStreamMessageIdentity.AppliesTo(endpoint).ShouldBe(derives);
    }

    private static JetStreamEnvelopeMapper theMapper()
    {
        return new JetStreamEnvelopeMapper(new NatsTransport().EndpointForSubject("orders"));
    }

    private static INatsJSMsg<byte[]> message(ulong streamSequence, ulong deliveries = 1, string? idHeader = null,
        Dictionary<string, string>? extraHeaders = null)
    {
        // What a publisher that is not Wolverine sends: a message type and a body, and no Wolverine id
        var headers = new NatsHeaders { ["message-type"] = "order.placed" };
        if (idHeader != null)
        {
            headers[EnvelopeConstants.IdKey] = idHeader;
        }

        foreach (var (key, value) in extraHeaders ?? [])
        {
            headers[key] = value;
        }

        var message = Substitute.For<INatsJSMsg<byte[]>>();
        message.Subject.Returns("orders");
        message.Data.Returns("{}"u8.ToArray());
        message.Headers.Returns(headers);

        // The consumer sequence and the delivery count change on a redelivery; the stream sequence does not
        message.Metadata.Returns(new NatsJSMsgMetadata(
            new NatsJSSequencePair(streamSequence, streamSequence + deliveries), deliveries, 0,
            DateTimeOffset.UtcNow, "ORDERS", "order-processor", ""));

        return message;
    }
}
