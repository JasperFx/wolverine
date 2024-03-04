using Shouldly;
using Wolverine;

namespace SqlServerTests.Persistence;

public class SqlServerBackedListenerTests : SqlServerBackedListenerContext
{
    protected override Task initialize()
    {
        return thePersistence.Admin.ClearAllAsync();
    }

    [Fact]
    public async Task handling_a_single_not_scheduled_envelope()
    {
        var envelope = notScheduledEnvelope();
        var persisted = (await afterReceivingTheEnvelopes()).Single();

        persisted.Status.ShouldBe(EnvelopeStatus.Incoming);
        persisted.OwnerId.ShouldBe(theSettings.AssignedNodeNumber);

        assertEnvelopeWasEnqueued(envelope);
    }

    [Fact]
    public async Task handling_a_single_scheduled_but_expired_envelope()
    {
        var envelope = scheduledButExpiredEnvelope();
        var persisted = (await afterReceivingTheEnvelopes()).Single();

        persisted.Status.ShouldBe(EnvelopeStatus.Incoming);
        persisted.OwnerId.ShouldBe(theSettings.AssignedNodeNumber);

        assertEnvelopeWasEnqueued(envelope);
    }
}