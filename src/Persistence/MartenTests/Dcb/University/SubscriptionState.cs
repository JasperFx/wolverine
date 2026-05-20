#region sample_wolverine_dcb_subscription_state
namespace MartenTests.Dcb.University;
/// Built from events tagged with BOTH CourseId and StudentId.
/// This is the core DCB pattern — the consistency boundary spans multiple streams.
///
/// Ported from the Axon SubscribeStudentToCourseCommandHandler.State which uses
/// EventCriteria.either() to load events matching CourseId OR StudentId.
/// </summary>
public partial class SubscriptionState
{
    // Required so the aggregate can be registered as a single-stream projection
    // (LiveStreamAggregation), which is what makes the JasperFx.Events source generator
    // emit the dispatcher that FetchForWritingByTags<SubscriptionState> resolves. For the
    // boundary (tag-query) path this Id is not stream-bound — it just satisfies the
    // single-stream projection shape, the same way Marten's own DCB aggregates carry one.
    public string Id { get; set; } = null!;
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
#endregion
