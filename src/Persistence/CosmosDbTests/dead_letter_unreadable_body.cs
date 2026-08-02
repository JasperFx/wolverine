using Shouldly;
using Wolverine.CosmosDb.Internals;
using Wolverine.Runtime;
using Xunit;

namespace CosmosDbTests;

/// <summary>
/// GH-3773 (CritterWatch#902's CosmosDb leg): a dead-letter document whose stored body was never
/// written by EnvelopeSerializer used to throw straight out of ToEnvelope() and abort the enclosing
/// query for the whole store. A bad document must cost the operator that document's body, not the
/// queue. Pure unit test — no emulator required.
/// </summary>
public class dead_letter_unreadable_body
{
    private static DeadLetterMessage messageWithBody(byte[] body) => new()
    {
        Id = "deadletter|11111111-2222-3333-4444-555555555555",
        EnvelopeId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        MessageType = "Some.Synthetic.Message",
        ReceivedAt = "local://durable/test",
        Source = "SomeService",
        ExceptionType = "System.InvalidOperationException",
        ExceptionMessage = "Synthetic dead letter",
        SentAt = DateTimeOffset.UtcNow,
        Body = body
    };

    [Fact]
    public void unreadable_body_degrades_to_a_self_identifying_placeholder()
    {
        var deadLetter = messageWithBody("{}"u8.ToArray()).ToEnvelope();

        deadLetter.Id.ShouldBe(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        deadLetter.MessageType.ShouldBe("Some.Synthetic.Message");
        deadLetter.Envelope.Message.ShouldBeOfType<PlaceHolder>();
        deadLetter.Envelope.Destination.ShouldBe(new Uri("local://durable/test"));
    }

    [Fact]
    public void empty_body_degrades_to_a_placeholder_instead_of_throwing()
    {
        var deadLetter = messageWithBody([]).ToEnvelope();

        deadLetter.Envelope.Message.ShouldBeOfType<PlaceHolder>();
    }

    [Fact]
    public void unparseable_received_at_leaves_destination_null()
    {
        var message = messageWithBody("{}"u8.ToArray());
        message.ReceivedAt = "not a uri";

        message.ToEnvelope().Envelope.Destination.ShouldBeNull();
    }
}
