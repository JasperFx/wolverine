using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests.Internals;

public class OutgoingMessage_mapping
{
    private readonly Envelope theEnvelope;
    private readonly Envelope theMappedEnvelope;

    public OutgoingMessage_mapping()
    {
        theEnvelope = ObjectMother.Envelope();
        theEnvelope.Id = Guid.NewGuid();
        theEnvelope.OwnerId = 3;
        theEnvelope.Attempts = 2;
        theEnvelope.Status = EnvelopeStatus.Handled;
        theEnvelope.DeliverBy = DateTime.Today.AddDays(2).ToUniversalTime();
        
        var message = new OutgoingMessage(theEnvelope);
        theMappedEnvelope = message.Read();
    }

    [Fact]
    public void map_the_deliver_by()
    {
        theMappedEnvelope.DeliverBy.ShouldBe(theEnvelope.DeliverBy);
    }
    
    [Fact]
    public void map_the_id()
    {
        theMappedEnvelope.Id.ShouldBe(theEnvelope.Id);
    }

    [Fact]
    public void map_the_owner_id()
    {
        theMappedEnvelope.OwnerId.ShouldBe(theEnvelope.OwnerId);
    }

    [Fact]
    public void map_the_execution_time()
    {
        theMappedEnvelope.ScheduledTime.ShouldBe(theEnvelope.ScheduledTime);
    }

    [Fact]
    public void map_the_attempts()
    {
        theMappedEnvelope.Attempts.ShouldBe(theEnvelope.Attempts);
    }

    [Fact]
    public void map_the_body()
    {
        theMappedEnvelope.Data.ShouldBe(theEnvelope.Data);
    }

    [Fact]
    public void map_the_message_type()
    {
        theMappedEnvelope.MessageType.ShouldBe(theEnvelope.MessageType);
    }

    [Fact]
    public void map_the_destination()
    {
        theMappedEnvelope.Destination.ShouldBe(theEnvelope.Destination);
    }
}