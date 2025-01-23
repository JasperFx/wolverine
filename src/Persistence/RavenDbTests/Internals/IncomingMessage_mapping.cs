using NSubstitute;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb.Internals;
using Wolverine.Runtime.Routing;

namespace RavenDbTests.Internals;

public class IncomingMessage_mapping
{
    private readonly Envelope theEnvelope;
    private readonly Envelope theMappedEnvelope;

    public IncomingMessage_mapping()
    {
        theEnvelope = ObjectMother.Envelope();
        theEnvelope.ScheduledTime = DateTime.Today.ToUniversalTime();
        theEnvelope.Id = Guid.NewGuid();
        theEnvelope.OwnerId = 3;
        theEnvelope.Attempts = 2;
        theEnvelope.Status = EnvelopeStatus.Handled;
        
        var message = new IncomingMessage(theEnvelope, new RavenDbMessageStore(Substitute.For<IDocumentStore>(), new WolverineOptions()));
        theMappedEnvelope = message.Read();
    }

    [Fact]
    public void map_the_id()
    {
        theMappedEnvelope.Id.ShouldBe(theEnvelope.Id);
    }

    [Fact]
    public void map_the_status()
    {
        theMappedEnvelope.Status.ShouldBe(EnvelopeStatus.Handled);
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