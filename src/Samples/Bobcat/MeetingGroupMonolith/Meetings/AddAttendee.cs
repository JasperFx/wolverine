using Marten;
using MeetingGroupMonolith;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Meetings;

public record AddAttendee(Guid MeetingId, Guid MemberId);

public static class AddAttendeeEndpoint
{
    public static ProblemDetails Validate(AddAttendee command, [Entity] Meeting? meeting)
    {
        if (meeting is null)
            return new ProblemDetails { Detail = "Meeting not found", Status = 404 };
        if (meeting.Status == MeetingStatus.Cancelled)
            return new ProblemDetails { Detail = "Cannot join a cancelled meeting", Status = 400 };
        if (meeting.AttendeesLimit.HasValue && meeting.Attendees.Count >= meeting.AttendeesLimit.Value)
            return new ProblemDetails { Detail = "Meeting is full", Status = 400 };
        if (meeting.Attendees.Any(a => a.MemberId == command.MemberId))
            return new ProblemDetails { Detail = "Already attending", Status = 409 };

        return WolverineContinue.NoProblems;
    }

    // Cascade: adding an attendee to a paid meeting publishes MeetingAttendeeAddedEvent
    // so the Payments module can create a MeetingFee
    [WolverinePost("/api/meetings/{meetingId}/attendees")]
    public static (Meeting, MeetingAttendeeAddedEvent) Post(
        AddAttendee command,
        [Entity("MeetingId", Required = true)] Meeting meeting,
        IDocumentSession session)
    {
        meeting.Attendees.Add(new MeetingAttendee
        {
            MemberId = command.MemberId,
            AddedAt = DateTimeOffset.UtcNow,
        });

        session.Store(meeting);

        return (meeting, new MeetingAttendeeAddedEvent(meeting.Id, command.MemberId, meeting.MeetingGroupId));
    }
}
