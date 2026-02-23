using Shouldly;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests;

public class SchedulingTokenTests
{
    [Fact]
    public void envelope_scheduling_token_is_null_by_default()
    {
        var envelope = new Envelope();
        envelope.SchedulingToken.ShouldBeNull();
    }

    [Fact]
    public void envelope_scheduling_token_can_be_set_to_long()
    {
        var envelope = new Envelope();
        envelope.SchedulingToken = 42L;
        envelope.SchedulingToken.ShouldBe(42L);
    }

    [Fact]
    public void envelope_scheduling_token_can_be_set_to_guid()
    {
        var envelope = new Envelope();
        var id = Guid.NewGuid();
        envelope.SchedulingToken = id;
        envelope.SchedulingToken.ShouldBe(id);
    }

    [Fact]
    public void schedule_result_contains_envelopes()
    {
        var envelopes = new[] { new Envelope(), new Envelope() };
        var result = new ScheduleResult(envelopes);
        result.Envelopes.Count.ShouldBe(2);
    }

    [Fact]
    public void schedule_result_with_empty_envelopes()
    {
        var result = new ScheduleResult(Array.Empty<Envelope>());
        result.Envelopes.Count.ShouldBe(0);
    }

    // --- ISenderWithScheduledCancellation interface tests ---

    [Fact]
    public void null_sender_is_not_ISenderWithScheduledCancellation()
    {
        ISender sender = new NullSender(new Uri("null://test"));
        (sender is ISenderWithScheduledCancellation).ShouldBeFalse();
    }
}
