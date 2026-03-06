using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;

namespace MartenTests.MultiStream.University;

public record UnsubscribeStudentFromCourse(StudentId StudentId, CourseId CourseId);

/// <summary>
/// Unsubscribes a student from a course. Idempotent if not subscribed.
/// Uses DCB to query by both CourseId AND StudentId tags.
/// </summary>
public static class UnsubscribeStudentHandler
{
    public static async Task Handle(UnsubscribeStudentFromCourse command, IDocumentSession session)
    {
        var query = new EventTagQuery()
            .Or<StudentSubscribedToCourse, CourseId>(command.CourseId)
            .Or<StudentUnsubscribedFromCourse, CourseId>(command.CourseId)
            .Or<StudentSubscribedToCourse, StudentId>(command.StudentId)
            .Or<StudentUnsubscribedFromCourse, StudentId>(command.StudentId);

        var boundary = await session.Events.FetchForWritingByTags<UnsubscriptionState>(query);

        if (boundary.Aggregate is not { Subscribed: true })
            return; // Not subscribed, nothing to do

        var @event = new StudentUnsubscribedFromCourse(FacultyId.Default, command.StudentId, command.CourseId);
        boundary.AppendOne(@event);
    }
}
