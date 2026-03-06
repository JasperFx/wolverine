#region sample_wolverine_dcb_university_events
namespace MartenTests.Dcb.University;

public record CourseCreated(FacultyId FacultyId, CourseId CourseId, string Name, int Capacity);

public record CourseRenamed(FacultyId FacultyId, CourseId CourseId, string Name);

public record CourseCapacityChanged(FacultyId FacultyId, CourseId CourseId, int Capacity);

public record StudentEnrolledInFaculty(FacultyId FacultyId, StudentId StudentId, string FirstName, string LastName);

public record StudentSubscribedToCourse(FacultyId FacultyId, StudentId StudentId, CourseId CourseId);

public record StudentUnsubscribedFromCourse(FacultyId FacultyId, StudentId StudentId, CourseId CourseId);

public record AllCoursesFullyBookedNotificationSent(FacultyId FacultyId);
#endregion
