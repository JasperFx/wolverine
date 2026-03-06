using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;

namespace MartenTests.MultiStream.University;

public record CreateCourse(CourseId CourseId, string Name, int Capacity);

/// <summary>
/// Creates a course if it doesn't already exist.
/// Uses DCB to query by CourseId tag to check for prior creation.
/// </summary>
public static class CreateCourseHandler
{
    public static async Task Handle(CreateCourse command, IDocumentSession session)
    {
        var query = new EventTagQuery().Or<CourseCreated, CourseId>(command.CourseId);
        var boundary = await session.Events.FetchForWritingByTags<CourseState>(query);

        if (boundary.Aggregate is { Created: true })
            return; // Already created, idempotent

        var @event = new CourseCreated(FacultyId.Default, command.CourseId, command.Name, command.Capacity);
        boundary.AppendOne(@event);
    }
}
