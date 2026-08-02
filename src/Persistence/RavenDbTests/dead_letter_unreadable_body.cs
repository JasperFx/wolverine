using Shouldly;
using Wolverine.RavenDb.Internals;
using Wolverine.Runtime;
using Xunit;

namespace RavenDbTests;

/// <summary>
/// GH-3773 (CritterWatch#902's RavenDb leg): a dead-letter document whose stored body was never
/// written by EnvelopeSerializer — hand-seeded raw JSON, or an incompatible serialization version —
/// used to throw straight out of ToEnvelope() and abort the enclosing query for the whole store,
/// hiding every readable dead letter behind the poison document. A bad document must cost the
/// operator that document's body, not the queue.
/// </summary>
public class dead_letter_unreadable_body
{
    private static DeadLetterMessage messageWithBody(byte[] body) => new()
    {
        Id = "dlq/11111111-2222-3333-4444-555555555555",
        EnvelopeId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        MessageType = "Some.Synthetic.Message",
        ReceivedAt = new Uri("local://durable/test"),
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
    public void null_sent_at_no_longer_throws()
    {
        var message = messageWithBody("{}"u8.ToArray());
        message.SentAt = null;

        Should.NotThrow(() => message.ToEnvelope());
    }
}
