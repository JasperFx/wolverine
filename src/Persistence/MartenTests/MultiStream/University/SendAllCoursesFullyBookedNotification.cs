using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;

namespace MartenTests.MultiStream.University;

public record SendAllCoursesFullyBookedNotification(FacultyId FacultyId);

/// <summary>
/// Automation handler that sends a notification when all courses are fully booked.
/// Uses DCB to query events tagged with FacultyId to build state across all courses.
///
/// In the Axon demo this is split into an EventHandler (reactor) and a CommandHandler.
/// Here we port both patterns as Wolverine handlers.
/// </summary>
public static class AllCoursesFullyBookedHandler
{
    public static async Task Handle(SendAllCoursesFullyBookedNotification command, IDocumentSession session)
    {
        var query = new EventTagQuery()
            .Or<CourseCreated, FacultyId>(command.FacultyId)
            .Or<CourseCapacityChanged, FacultyId>(command.FacultyId)
            .Or<StudentSubscribedToCourse, FacultyId>(command.FacultyId)
            .Or<StudentUnsubscribedFromCourse, FacultyId>(command.FacultyId)
            .Or<AllCoursesFullyBookedNotificationSent, FacultyId>(command.FacultyId);

        var boundary = await session.Events.FetchForWritingByTags<AllCoursesFullyBookedState>(query);

        var state = boundary.Aggregate;
        if (state is { AllCoursesFullyBooked: true, Notified: false })
        {
            // In a real app, send notification via INotificationService here
            boundary.AppendOne(new AllCoursesFullyBookedNotificationSent(command.FacultyId));
        }
    }
}
