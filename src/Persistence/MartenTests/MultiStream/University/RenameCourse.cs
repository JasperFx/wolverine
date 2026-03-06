using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;

namespace MartenTests.MultiStream.University;

public record RenameCourse(CourseId CourseId, string Name);

/// <summary>
/// Renames a course. Validates the course exists and name is different.
/// Uses DCB to query by CourseId tag.
/// </summary>
public static class RenameCourseHandler
{
    public static async Task Handle(RenameCourse command, IDocumentSession session)
    {
        var query = new EventTagQuery()
            .Or<CourseCreated, CourseId>(command.CourseId)
            .Or<CourseRenamed, CourseId>(command.CourseId);
        var boundary = await session.Events.FetchForWritingByTags<CourseState>(query);

        var state = boundary.Aggregate;
        if (state is not { Created: true })
            throw new InvalidOperationException("Course with given id does not exist");

        if (command.Name == state.Name)
            return; // No change needed

        var @event = new CourseRenamed(FacultyId.Default, command.CourseId, command.Name);
        boundary.AppendOne(@event);
    }
}
