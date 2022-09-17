using System;
using Wolverine;
using Wolverine.Runtime.Interop.MassTransit;
using Shouldly;
using Xunit;

namespace InteroperabilityTests.MassTransit;

public class MassTransitEnvelopeTests
{
    private readonly DateTimeOffset theSentTime = new DateTimeOffset(new DateTime(2022, 9, 13, 5, 0, 0));
    private readonly DateTimeOffset theExpirationTime = new DateTimeOffset(new DateTime(2022, 9, 13, 5, 5, 0));
    private readonly Envelope theEnvelope = new Envelope();
    private readonly MassTransitEnvelope theMassTransitEnvelope;

    public MassTransitEnvelopeTests()
    {
        theMassTransitEnvelope = new MassTransitEnvelope
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString(),
            ExpirationTime = theExpirationTime.DateTime.ToUniversalTime(),
            SentTime = theSentTime.DateTime.ToUniversalTime()
        };

        theMassTransitEnvelope.Headers.Add("color", "purple");
        theMassTransitEnvelope.Headers.Add("number", 1);

        theMassTransitEnvelope.TransferData(theEnvelope);

    }

    [Fact]
    public void create_masstransit_envelope_from_envelope()
    {
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid(),
            DeliverBy = new DateTimeOffset(new DateTime(2022, 9, 14)),
            Message = new object()
        };

        var mtEnvelope = new MassTransitEnvelope(envelope);

        mtEnvelope.MessageId.ShouldBe(envelope.Id.ToString());
        mtEnvelope.CorrelationId.ShouldBe(envelope.CorrelationId);
        mtEnvelope.ConversationId.ShouldBe(envelope.ConversationId.ToString());
        mtEnvelope.SentTime.ShouldNotBeNull();

        mtEnvelope.ExpirationTime.Value.ShouldBe(envelope.DeliverBy.Value.DateTime);

    }

    [Fact]
    public void map_headers()
    {
        theEnvelope.Headers["color"].ShouldBe("purple");
        theEnvelope.Headers["number"].ShouldBe("1");
    }

    [Fact]
    public void map_the_message_id()
    {
        theEnvelope.Id.ShouldBe(Guid.Parse(theMassTransitEnvelope.MessageId));
    }

    [Fact]
    public void map_the_correlation_id()
    {
        theEnvelope.CorrelationId.ShouldBe(theMassTransitEnvelope.CorrelationId);
    }

    [Fact]
    public void map_the_conversation_id()
    {
        theEnvelope.ConversationId.ShouldBe(Guid.Parse(theMassTransitEnvelope.ConversationId));
    }

    [Fact]
    public void map_the_expiration_time()
    {
        theEnvelope.DeliverBy.ShouldBe(theExpirationTime);
    }

    [Fact]
    public void map_the_sent_time()
    {
        theEnvelope.SentAt.ShouldBe(theSentTime);
    }
}
