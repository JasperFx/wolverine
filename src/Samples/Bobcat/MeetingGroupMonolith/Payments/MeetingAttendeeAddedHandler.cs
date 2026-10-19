using Marten;
using MeetingGroupMonolith;

namespace Payments;

/// <summary>
/// Handles MeetingAttendeeAddedEvent from the Meetings module.
/// Creates a MeetingFee in the Payments event store.
/// Replaces: InboxMessage → ProcessInboxJob → MediatR → handler + SqlStreamStore
/// </summary>
public static class MeetingAttendeeAddedHandler
{
    public static void Handle(MeetingAttendeeAddedEvent message, IDocumentSession session)
    {
        var feeId = Guid.NewGuid();
        session.Events.StartStream(
            feeId,
            new MeetingFeeCreated(feeId, message.AttendeeId, message.MeetingId, 0m));
    }
}
