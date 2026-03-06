using JasperFx.Events.Tags;
using Wolverine.Marten;

namespace MartenTests.Dcb.University;

public record BoundaryModelSubscribeStudentToCourse(StudentId StudentId, CourseId CourseId);

#region sample_wolverine_dcb_boundary_model_handler
public static class BoundaryModelSubscribeStudentHandler
{
    public const int MaxCoursesPerStudent = 3;

    public static EventTagQuery Load(BoundaryModelSubscribeStudentToCourse command)
        => EventTagQuery
            .For(command.CourseId)
            .AndEventsOfType<CourseCreated, CourseCapacityChanged, StudentSubscribedToCourse, StudentUnsubscribedFromCourse>()
            .Or(command.StudentId)
            .AndEventsOfType<StudentEnrolledInFaculty, StudentSubscribedToCourse, StudentUnsubscribedFromCourse>();

    public static StudentSubscribedToCourse Handle(
        BoundaryModelSubscribeStudentToCourse command,
        [BoundaryModel]
        SubscriptionState state)
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

        return new StudentSubscribedToCourse(FacultyId.Default, command.StudentId, command.CourseId);
    }
}
#endregion
