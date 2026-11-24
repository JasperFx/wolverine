using Shouldly;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Xunit;

namespace CoreTests.Testing;

/// <summary>
/// GH-5760 / GH-5676. PostgresqlTests runs its "marten" collection beside the default one, so two tests can track
/// recovered messages at the same time. Each must see its own messages whichever opened its session last. With one
/// process-wide session, the later test took the earlier one's messages and
/// partitioned_local_queue_inbox_recovery reported "only saw 0" while every row had been recovered.
/// </summary>
public class recovered_message_tracking_sessions_5760
{
    [Fact]
    public void an_overlapping_session_does_not_take_another_sessions_messages()
    {
        using var first = RecoveredMessages.Track();
        var mine = new RecoveredMessage(Guid.NewGuid(), 0);

        // A concurrent test opens its session after this one, and then this test's message is handled
        using var second = RecoveredMessages.Track();
        RecoveredMessageHandler.Handle(mine);

        first.Contains(mine.Id).ShouldBeTrue();
        first.CountOf([mine.Id]).ShouldBe(1);
        second.Contains(mine.Id).ShouldBeTrue();
    }

    [Fact]
    public void a_disposed_session_stops_recording()
    {
        var closed = RecoveredMessages.Track();
        closed.Dispose();

        using var open = RecoveredMessages.Track();
        var message = new RecoveredMessage(Guid.NewGuid(), 1);
        RecoveredMessageHandler.Handle(message);

        closed.Contains(message.Id).ShouldBeFalse();
        open.Contains(message.Id).ShouldBeTrue();
    }
}
