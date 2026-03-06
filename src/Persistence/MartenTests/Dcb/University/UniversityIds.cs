#region sample_wolverine_dcb_university_ids
namespace MartenTests.Dcb.University;

/// <summary>
/// Strong-typed ID for a course. Uses string value with "Course:" prefix.
/// </summary>
public record CourseId(string Value)
{
    public static CourseId Random() => new($"Course:{Guid.NewGuid()}");
    public static CourseId Of(string raw) => new(raw.StartsWith("Course:") ? raw : $"Course:{raw}");
    public override string ToString() => Value;
}

/// <summary>
/// Strong-typed ID for a student. Uses string value with "Student:" prefix.
/// </summary>
public record StudentId(string Value)
{
    public static StudentId Random() => new($"Student:{Guid.NewGuid()}");
    public static StudentId Of(string raw) => new(raw.StartsWith("Student:") ? raw : $"Student:{raw}");
    public override string ToString() => Value;
}

/// <summary>
/// Strong-typed ID for the faculty. Single-instance in this demo.
/// </summary>
public record FacultyId(string Value)
{
    public static readonly FacultyId Default = new("Faculty:ONLY_FACULTY_ID");
    public static FacultyId Of(string raw) => new(raw.StartsWith("Faculty:") ? raw : $"Faculty:{raw}");
    public override string ToString() => Value;
}

/// <summary>
/// Composite ID for a student-course subscription.
/// </summary>
public record SubscriptionId(CourseId CourseId, StudentId StudentId);
#endregion
