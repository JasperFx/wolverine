namespace MartenTests.MultiStream.University;

/// <summary>
/// Aggregate state for a single course, built from events tagged with CourseId.
/// Used by CreateCourse, RenameCourse, and ChangeCourseCapacity handlers.
/// </summary>
public class CourseState
{
    public bool Created { get; private set; }
    public string? Name { get; private set; }
    public int Capacity { get; private set; }

    public void Apply(CourseCreated e)
    {
        Created = true;
        Name = e.Name;
        Capacity = e.Capacity;
    }

    public void Apply(CourseRenamed e)
    {
        Name = e.Name;
    }

    public void Apply(CourseCapacityChanged e)
    {
        Capacity = e.Capacity;
    }
}
