using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests.Internals;

public class DeadLetterMessage_mapping
{
    private readonly Envelope theEnvelope;
    private readonly Exception theException = new InvalidOperationException();
    private readonly DeadLetterMessage theMessage;

    public DeadLetterMessage_mapping()
    {
        theEnvelope = ObjectMother.Envelope();
        theEnvelope.ScheduledTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        
        theMessage = new DeadLetterMessage(theEnvelope, theException);
    }

    [Fact]
    public void map_the_id()
    {
        theMessage.Id.ShouldBe("dlq/" + theEnvelope.Id.ToString());
    }

    [Fact]
    public void map_the_message_type()
    {
        theMessage.MessageType.ShouldBe(theEnvelope.MessageType);
    }

    [Fact]
    public void map_received_at()
    {
        theMessage.ReceivedAt.ShouldBe(theEnvelope.Destination);
    }

    [Fact]
    public void map_sent_at()
    {
        theMessage.SentAt.ShouldBe(theEnvelope.SentAt);
    }

    [Fact]
    public void map_source()
    {
        theMessage.Source.ShouldBe(theEnvelope.Source);
    }

    [Fact]
    public void map_exception_type()
    {
        theMessage.ExceptionType.ShouldBe(theException.GetType().FullName);
    }

    [Fact]
    public void map_exception_message()
    {
        theMessage.ExceptionMessage.ShouldBe(theException.Message);
    }
}