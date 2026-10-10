using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Scheduling;

public class reference_key_metadata
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void supplied_key_must_not_be_empty(string key)
    {
        Should.Throw<ArgumentException>(() => new DeliveryOptions { ReferenceKey = key });
        Should.Throw<ArgumentException>(() => new Envelope { ReferenceKey = key });
    }

    [Fact]
    public void optional_key_is_metadata_and_round_trips_without_changing_payload()
    {
        var envelope = ObjectMother.Envelope();
        var data = envelope.Data!.ToArray();
        new DeliveryOptions { ReferenceKey = "Course/1" }.Override(envelope);
        var incoming = EnvelopeSerializer.Deserialize(EnvelopeSerializer.Serialize(envelope));
        incoming.ReferenceKey.ShouldBe("Course/1");
        incoming.Data.ShouldBe(data);
        incoming.ReferenceKey = null;
        incoming.ReferenceKey.ShouldBeNull();
    }

    [Fact]
    public void omitted_key_does_not_allocate_headers()
    {
        var envelope = new Envelope();
        new DeliveryOptions().Override(envelope);
        envelope.ReferenceKey.ShouldBeNull();
        envelope.HasHeaders.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task unsupported_filters_cannot_silently_broaden_queries_or_cancellation(bool reference)
    {
        var query = reference
            ? new ScheduledMessageQuery { ReferenceKey = "account/1" }
            : new ScheduledMessageQuery { TenantId = "tenant-a" };
        var schedules = new NullMessageStore().ScheduledMessages;
        await Should.ThrowAsync<NotSupportedException>(() => schedules.QueryAsync(query, CancellationToken.None));
        await Should.ThrowAsync<NotSupportedException>(() => schedules.CancelAsync(query, CancellationToken.None));
    }

}
