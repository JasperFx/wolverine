namespace MartenTests.MultiStream.University;

/// <summary>
/// State for unsubscribe — tracks whether the student is currently subscribed
/// to the course. Built from events tagged with both CourseId AND StudentId.
/// </summary>
public class UnsubscriptionState
{
    public bool Subscribed { get; private set; }

    public void Apply(StudentSubscribedToCourse e)
    {
        Subscribed = true;
    }

    public void Apply(StudentUnsubscribedFromCourse e)
    {
        Subscribed = false;
    }
}
