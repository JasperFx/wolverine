namespace MartenTests.MultiStream.University;

/// <summary>
/// Cross-stream aggregate state for a student subscribing to a course.
/// Built from events tagged with BOTH CourseId and StudentId.
/// This is the core DCB pattern — the consistency boundary spans multiple streams.
///
/// Ported from the Axon SubscribeStudentToCourseCommandHandler.State which uses
/// EventCriteria.either() to load events matching CourseId OR StudentId.
/// </summary>
public class SubscriptionState
{
    public CourseId? CourseId { get; private set; }
    public int CourseCapacity { get; private set; }
    public int StudentsSubscribedToCourse { get; private set; }

    public StudentId? StudentId { get; private set; }
    public int CoursesStudentSubscribed { get; private set; }
    public bool AlreadySubscribed { get; private set; }

    public void Apply(CourseCreated e)
    {
        CourseId = e.CourseId;
        CourseCapacity = e.Capacity;
    }

    public void Apply(CourseCapacityChanged e)
    {
        CourseCapacity = e.Capacity;
    }

    public void Apply(StudentEnrolledInFaculty e)
    {
        StudentId = e.StudentId;
    }

    public void Apply(StudentSubscribedToCourse e)
    {
        if (e.CourseId == CourseId)
            StudentsSubscribedToCourse++;
        if (e.StudentId == StudentId)
            CoursesStudentSubscribed++;
        if (e.StudentId == StudentId && e.CourseId == CourseId)
            AlreadySubscribed = true;
    }

    public void Apply(StudentUnsubscribedFromCourse e)
    {
        if (e.CourseId == CourseId)
            StudentsSubscribedToCourse--;
        if (e.StudentId == StudentId)
            CoursesStudentSubscribed--;
        if (e.StudentId == StudentId && e.CourseId == CourseId)
            AlreadySubscribed = false;
    }
}
