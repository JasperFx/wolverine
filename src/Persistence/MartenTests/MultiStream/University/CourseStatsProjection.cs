namespace MartenTests.MultiStream.University;

/// <summary>
/// Read model for course statistics. Ported from the Axon CoursesStatsProjection.
/// In Marten, this would be an inline or async projection.
/// </summary>
public class CourseStatsReadModel
{
    public CourseId CourseId { get; set; } = default!;
    public string Name { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public int SubscribedStudents { get; set; }
}

public static class CourseStatsProjection
{
    public static CourseStatsReadModel Create(CourseCreated e) =>
        new()
        {
            CourseId = e.CourseId,
            Name = e.Name,
            Capacity = e.Capacity,
            SubscribedStudents = 0
        };

    public static void Apply(CourseRenamed e, CourseStatsReadModel model) =>
        model.Name = e.Name;

    public static void Apply(CourseCapacityChanged e, CourseStatsReadModel model) =>
        model.Capacity = e.Capacity;

    public static void Apply(StudentSubscribedToCourse e, CourseStatsReadModel model) =>
        model.SubscribedStudents++;

    public static void Apply(StudentUnsubscribedFromCourse e, CourseStatsReadModel model) =>
        model.SubscribedStudents--;
}
