namespace MartenTests.MultiStream.University;

/// <summary>
/// Aggregate state for student enrollment, built from events tagged with StudentId.
/// Used by EnrollStudentInFaculty handler.
/// </summary>
public class EnrolledStudentState
{
    public bool Exists { get; private set; }

    public void Apply(StudentEnrolledInFaculty e)
    {
        Exists = true;
    }
}
