using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;

namespace MartenTests.MultiStream.University;

public record ChangeCourseCapacity(CourseId CourseId, int Capacity);

/// <summary>
/// Changes a course's capacity. Validates the course exists and capacity differs.
/// Uses DCB to query by CourseId tag.
/// </summary>
public static class ChangeCourseCapacityHandler
{
    public static async Task Handle(ChangeCourseCapacity command, IDocumentSession session)
    {
        var query = new EventTagQuery()
            .Or<CourseCreated, CourseId>(command.CourseId)
            .Or<CourseCapacityChanged, CourseId>(command.CourseId);
        var boundary = await session.Events.FetchForWritingByTags<CourseState>(query);

        var state = boundary.Aggregate;
        if (state is not { Created: true })
            throw new InvalidOperationException("Course with given id does not exist");

        if (command.Capacity == state.Capacity)
            return; // No change needed

        var @event = new CourseCapacityChanged(FacultyId.Default, command.CourseId, command.Capacity);
        boundary.AppendOne(@event);
    }
}
