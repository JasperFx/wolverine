using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;

namespace MartenTests.Dcb.University;

public record SubscribeStudentToCourse(StudentId StudentId, CourseId CourseId);

/// <summary>
/// Subscribes a student to a course. This is the most complex DCB handler —
/// it spans BOTH CourseId and StudentId tag boundaries to enforce:
///   - Student must be enrolled in faculty
///   - Student can't subscribe to more than 3 courses
///   - Course must exist
///   - Course must have vacant spots
///   - Student can't already be subscribed to this course
///
/// Ported from the Axon demo's EventCriteria.either() pattern which OR's
/// events matching CourseId with events matching StudentId.
/// </summary>
public static class SubscribeStudentHandler
{
    public const int MaxCoursesPerStudent = 3;

    public static async Task Handle(SubscribeStudentToCourse command, IDocumentSession session)
    {
        // Query events tagged with CourseId OR StudentId — the DCB spans both
        var query = new EventTagQuery()
            .Or<CourseCreated, CourseId>(command.CourseId)
            .Or<CourseCapacityChanged, CourseId>(command.CourseId)
            .Or<StudentSubscribedToCourse, CourseId>(command.CourseId)
            .Or<StudentUnsubscribedFromCourse, CourseId>(command.CourseId)
            .Or<StudentEnrolledInFaculty, StudentId>(command.StudentId)
            .Or<StudentSubscribedToCourse, StudentId>(command.StudentId)
            .Or<StudentUnsubscribedFromCourse, StudentId>(command.StudentId);

        var boundary = await session.Events.FetchForWritingByTags<SubscriptionState>(query);

        var state = boundary.Aggregate ?? new SubscriptionState();
        Decide(command, state);

        var @event = new StudentSubscribedToCourse(FacultyId.Default, command.StudentId, command.CourseId);
        boundary.AppendOne(@event);
    }

    private static void Decide(SubscribeStudentToCourse command, SubscriptionState state)
    {
        if (state.StudentId == null)
            throw new InvalidOperationException("Student with given id never enrolled the faculty");

        if (state.CoursesStudentSubscribed >= MaxCoursesPerStudent)
            throw new InvalidOperationException("Student subscribed to too many courses");

        if (state.CourseId == null)
            throw new InvalidOperationException("Course with given id does not exist");

        if (state.StudentsSubscribedToCourse >= state.CourseCapacity)
            throw new InvalidOperationException("Course is fully booked");

        if (state.AlreadySubscribed)
            throw new InvalidOperationException("Student already subscribed to this course");
    }
}
