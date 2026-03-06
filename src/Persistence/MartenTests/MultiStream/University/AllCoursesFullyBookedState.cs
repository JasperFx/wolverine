namespace MartenTests.MultiStream.University;

/// <summary>
/// State for the "all courses fully booked" automation.
/// Tracks all courses and their capacity/subscription counts.
/// Built from events tagged with FacultyId.
/// </summary>
public class AllCoursesFullyBookedState
{
    public Dictionary<CourseId, CourseStats> Courses { get; } = new();
    public bool Notified { get; private set; }

    public bool AllCoursesFullyBooked =>
        Courses.Count > 0 && Courses.Values.All(c => c.IsFullyBooked);

    public void Apply(CourseCreated e)
    {
        Courses[e.CourseId] = new CourseStats(e.Capacity, 0);
        ResetNotifiedIfNotAllBooked();
    }

    public void Apply(CourseCapacityChanged e)
    {
        if (Courses.TryGetValue(e.CourseId, out var stats))
            Courses[e.CourseId] = stats with { Capacity = e.Capacity };
        ResetNotifiedIfNotAllBooked();
    }

    public void Apply(StudentSubscribedToCourse e)
    {
        if (Courses.TryGetValue(e.CourseId, out var stats))
            Courses[e.CourseId] = stats with { Students = stats.Students + 1 };
        ResetNotifiedIfNotAllBooked();
    }

    public void Apply(StudentUnsubscribedFromCourse e)
    {
        if (Courses.TryGetValue(e.CourseId, out var stats))
            Courses[e.CourseId] = stats with { Students = stats.Students - 1 };
        ResetNotifiedIfNotAllBooked();
    }

    public void Apply(AllCoursesFullyBookedNotificationSent e)
    {
        Notified = true;
    }

    private void ResetNotifiedIfNotAllBooked()
    {
        if (!AllCoursesFullyBooked)
            Notified = false;
    }

    public record CourseStats(int Capacity, int Students)
    {
        public bool IsFullyBooked => Students >= Capacity;
    }
}
