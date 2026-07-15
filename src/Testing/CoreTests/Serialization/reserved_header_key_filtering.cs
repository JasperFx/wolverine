using System.Reflection;
using JasperFx.Core;
using Shouldly;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

/// <summary>
///     Regression coverage for GH-3408. A reserved key sitting in the loose <see cref="Envelope.Headers" />
///     used to be appended to the wire format *after* the typed properties, and the reader promotes reserved
///     keys straight back into those typed properties -- so the stray header silently overwrote the real
///     TenantId / SagaId / Id / MessageType on the next trip through the durable inbox, outbox, or scheduled
///     message store.
/// </summary>
public class reserved_header_key_filtering
{
    private static Envelope roundTrip(Envelope outgoing)
    {
        return EnvelopeSerializer.Deserialize(EnvelopeSerializer.Serialize(outgoing));
    }

    private static Envelope theOutgoingEnvelope()
    {
        return new Envelope
        {
            SentAt = DateTime.Today.ToUniversalTime(),
            Data = [1, 2, 3],
            Destination = "tcp://localhost:2222/incoming".ToUri(),
            MessageType = "real-message-type",
            TenantId = "real-tenant",
            SagaId = "real-saga",
            Id = Guid.NewGuid()
        };
    }

    [Fact]
    public void a_tenant_id_header_does_not_overwrite_the_typed_tenant_id()
    {
        var outgoing = theOutgoingEnvelope();
        outgoing.Headers[EnvelopeConstants.TenantIdKey] = "hijacked-tenant";

        var incoming = roundTrip(outgoing);

        incoming.TenantId.ShouldBe("real-tenant");
        incoming.Headers.ContainsKey(EnvelopeConstants.TenantIdKey).ShouldBeFalse();
    }

    [Fact]
    public void a_saga_id_header_does_not_overwrite_the_typed_saga_id()
    {
        var outgoing = theOutgoingEnvelope();
        outgoing.Headers[EnvelopeConstants.SagaIdKey] = "hijacked-saga";

        var incoming = roundTrip(outgoing);

        incoming.SagaId.ShouldBe("real-saga");
    }

    [Fact]
    public void an_id_header_does_not_overwrite_the_envelope_id()
    {
        var outgoing = theOutgoingEnvelope();
        outgoing.Headers[EnvelopeConstants.IdKey] = Guid.NewGuid().ToString();

        var incoming = roundTrip(outgoing);

        incoming.Id.ShouldBe(outgoing.Id);
    }

    [Fact]
    public void a_message_type_header_does_not_overwrite_the_typed_message_type()
    {
        var outgoing = theOutgoingEnvelope();
        outgoing.Headers[EnvelopeConstants.MessageTypeKey] = "hijacked-message-type";

        var incoming = roundTrip(outgoing);

        incoming.MessageType.ShouldBe("real-message-type");
    }

    [Fact]
    public void all_the_reserved_keys_at_once_leave_every_typed_property_alone()
    {
        var outgoing = theOutgoingEnvelope();
        foreach (var key in EnvelopeSerializer.ReservedHeaderKeys)
        {
            outgoing.Headers[key] = "hijacked";
        }

        var incoming = roundTrip(outgoing);

        incoming.TenantId.ShouldBe("real-tenant");
        incoming.SagaId.ShouldBe("real-saga");
        incoming.MessageType.ShouldBe("real-message-type");
        incoming.Id.ShouldBe(outgoing.Id);
        incoming.Destination.ShouldBe(outgoing.Destination);
    }

    [Fact]
    public void non_reserved_custom_headers_still_round_trip()
    {
        var outgoing = theOutgoingEnvelope();
        outgoing.Headers["name"] = "Jeremy";
        outgoing.Headers["state"] = "Texas";
        outgoing.Headers[EnvelopeConstants.TenantIdKey] = "hijacked-tenant";

        var incoming = roundTrip(outgoing);

        incoming.Headers["name"].ShouldBe("Jeremy");
        incoming.Headers["state"].ShouldBe("Texas");
    }

    [Fact]
    public void the_causation_id_header_is_not_filtered_out()
    {
        // DeliveryOptions deliberately stashes CausationId as a loose header, and the reader
        // never promotes it to a typed property -- so it must NOT be treated as reserved.
        var outgoing = theOutgoingEnvelope();
        outgoing.Headers[EnvelopeConstants.CausationIdKey] = "the-cause";

        roundTrip(outgoing).Headers[EnvelopeConstants.CausationIdKey].ShouldBe("the-cause");
    }

    /// <summary>
    ///     Guards the invariant directly rather than trusting a hand-maintained list: every
    ///     <see cref="EnvelopeConstants" /> key that the reader promotes into a typed property
    ///     (i.e. one that does NOT land in Headers) must be in the write-side reserved filter.
    ///     A newly added constant with a reader case that nobody adds to the filter fails here.
    /// </summary>
    [Fact]
    public void every_key_promoted_by_the_reader_is_in_the_reserved_set()
    {
        var keys = typeof(EnvelopeConstants)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(x => (string)x.GetRawConstantValue()!);

        foreach (var key in keys)
        {
            var envelope = new Envelope();

            var promoted = false;
            try
            {
                EnvelopeSerializer.ReadDataElement(envelope, key, "1");

                // Anything the reader handled by a real case never lands in Headers
                promoted = !envelope.Headers.ContainsKey(key);
            }
            catch (InvalidOperationException)
            {
                // The reader hit a typed case and choked on the sample value (a Uri or a date,
                // say). Reaching a typed case at all means the key is promoted.
                promoted = true;
            }

            if (promoted)
            {
                EnvelopeSerializer.ReservedHeaderKeys.ShouldContain(key,
                    $"'{key}' is promoted into a typed Envelope property by the reader, so it must be " +
                    "skipped when Envelope.Headers is written to the wire format. See GH-3408.");
            }
        }
    }
}
