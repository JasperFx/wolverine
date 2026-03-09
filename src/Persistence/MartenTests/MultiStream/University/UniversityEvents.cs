namespace MartenTests.MultiStream.University;

// All events carry their tag IDs as properties, matching the Axon @EventTag pattern.
// In Marten, tags are attached to events via WithTag() at append time.

public record CourseCreated(FacultyId FacultyId, CourseId CourseId, string Name, int Capacity);

public record CourseRenamed(FacultyId FacultyId, CourseId CourseId, string Name);

public record CourseCapacityChanged(FacultyId FacultyId, CourseId CourseId, int Capacity);

public record StudentEnrolledInFaculty(FacultyId FacultyId, StudentId StudentId, string FirstName, string LastName);

public record StudentSubscribedToCourse(FacultyId FacultyId, StudentId StudentId, CourseId CourseId);

public record StudentUnsubscribedFromCourse(FacultyId FacultyId, StudentId StudentId, CourseId CourseId);

public record AllCoursesFullyBookedNotificationSent(FacultyId FacultyId);
