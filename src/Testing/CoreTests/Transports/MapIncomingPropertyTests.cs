using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports;

public class MapIncomingPropertyTests
{
    [Fact]
    public void uses_the_custom_incoming_mapping()
    {
        var expectedReplyUri = new Uri("stub://test:123/");
        var mapper = new StubEnvelopeMapper(new StubEndpoint());
        mapper.MapIncomingProperty(x => x.ReplyUri!, (envelope, incoming) =>
        {
            envelope.ReplyUri = new Uri(incoming.SpecialReply!);
        });

        var envelope = new Envelope();
        mapper.MapIncomingToEnvelope(envelope, new StubTransportMessage
        {
            SpecialReply = expectedReplyUri.ToString()
        });

        envelope.ReplyUri.ShouldBe(expectedReplyUri);
    }

    // Regression coverage for https://github.com/JasperFx/wolverine/issues/2551.
    //
    // MapIncomingProperty / MapOutgoingProperty customize a single direction.
    // They must NOT disturb the opposite direction's default header mapping
    // (registered via MapPropertyToHeader during the mapper's constructor).
    //
    // The bug was that compileIncoming/compileOutgoing had their filter
    // predicates swapped — so MapIncomingProperty silently deleted the
    // outgoing header mapping and vice versa.

    [Fact]
    public void custom_incoming_property_preserves_default_outgoing_header()
    {
        var mapper = new StubEnvelopeMapper(new StubEndpoint());

        // Customize only the incoming direction for Id — read it from a
        // transport-specific location. This must not stop Id from being
        // written as the "id" header on outgoing messages.
        mapper.MapIncomingProperty(x => x.Id, (envelope, incoming) =>
        {
            if (incoming.Headers.TryGetValue("custom-id", out var raw) && Guid.TryParse(raw, out var parsed))
            {
                envelope.Id = parsed;
            }
        });

        var envelope = new Envelope { Id = Guid.NewGuid() };
        var outgoing = new StubTransportMessage();

        mapper.MapEnvelopeToOutgoing(envelope, outgoing);

        outgoing.Headers.ContainsKey(EnvelopeConstants.IdKey).ShouldBeTrue(
            "MapIncomingProperty(x => x.Id, ...) must not delete the default outgoing 'id' header mapping");
        outgoing.Headers[EnvelopeConstants.IdKey].ShouldBe(envelope.Id.ToString());
    }

    [Fact]
    public void custom_outgoing_property_preserves_default_incoming_header_read()
    {
        var mapper = new StubEnvelopeMapper(new StubEndpoint());

        // Customize only the outgoing direction for Id — write it to a
        // transport-specific location. This must not stop Id from being
        // read from the default "id" header on incoming messages.
        mapper.MapOutgoingProperty(x => x.Id, (envelope, outgoing) =>
        {
            outgoing.Headers["custom-id"] = envelope.Id.ToString();
        });

        var expected = Guid.NewGuid();
        var envelope = new Envelope();
        mapper.MapIncomingToEnvelope(envelope, new StubTransportMessage
        {
            Headers = { [EnvelopeConstants.IdKey] = expected.ToString() }
        });

        envelope.Id.ShouldBe(expected);
    }

    [Fact]
    public void custom_incoming_property_still_overrides_its_own_direction()
    {
        // Sanity check: the customization itself still works on the direction
        // it actually targets. This complements
        // custom_incoming_property_preserves_default_outgoing_header so a future
        // regression that over-corrects can't slip past.
        var mapper = new StubEnvelopeMapper(new StubEndpoint());

        mapper.MapIncomingProperty(x => x.Id, (envelope, incoming) =>
        {
            if (incoming.Headers.TryGetValue("custom-id", out var raw) && Guid.TryParse(raw, out var parsed))
            {
                envelope.Id = parsed;
            }
        });

        var expected = Guid.NewGuid();
        var envelope = new Envelope();
        mapper.MapIncomingToEnvelope(envelope, new StubTransportMessage
        {
            Headers = { ["custom-id"] = expected.ToString() }
        });

        envelope.Id.ShouldBe(expected);
    }

    [Fact]
    public void custom_outgoing_property_still_overrides_its_own_direction()
    {
        var mapper = new StubEnvelopeMapper(new StubEndpoint());

        mapper.MapOutgoingProperty(x => x.Id, (envelope, outgoing) =>
        {
            outgoing.Headers["custom-id"] = envelope.Id.ToString();
        });

        var envelope = new Envelope { Id = Guid.NewGuid() };
        var outgoing = new StubTransportMessage();

        mapper.MapEnvelopeToOutgoing(envelope, outgoing);

        outgoing.Headers.ContainsKey("custom-id").ShouldBeTrue();
        outgoing.Headers["custom-id"].ShouldBe(envelope.Id.ToString());
    }
}

internal class StubTransportMessage
{
    public Dictionary<string, string?> Headers { get; } = new();
    public string? SpecialReply { get; set; }
}

internal class StubEnvelopeMapper(Endpoint endpoint)
    : EnvelopeMapper<StubTransportMessage, StubTransportMessage>(endpoint)
{
    protected override void writeOutgoingHeader(StubTransportMessage outgoing, string key, string value)
    {
        outgoing.Headers[key] = value;
    }

    protected override bool tryReadIncomingHeader(StubTransportMessage incoming, string key, out string? value)
    {
        return incoming.Headers.TryGetValue(key, out value);
    }
}

internal class StubEndpoint() : Endpoint(new Uri("stub://mapper"), EndpointRole.Application)
{
    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }
}
