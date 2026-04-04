using JasperFx.Events;
using JasperFx.Events.Tags;
using Wolverine.Marten;

namespace MartenTests.Dcb.University;

public record BoundaryModelSubscribeStudentToCourse(StudentId StudentId, CourseId CourseId);

#region sample_wolverine_dcb_boundary_model_handler
public static class BoundaryModelSubscribeStudentHandler
{
    public const int MaxCoursesPerStudent = 3;

    public static EventTagQuery Load(BoundaryModelSubscribeStudentToCourse command)
        => EventTagQuery
            .For(command.CourseId)
            .AndEventsOfType<CourseCreated, CourseCapacityChanged, StudentSubscribedToCourse, StudentUnsubscribedFromCourse>()
            .Or(command.StudentId)
            .AndEventsOfType<StudentEnrolledInFaculty, StudentSubscribedToCourse, StudentUnsubscribedFromCourse>();

    public static StudentSubscribedToCourse Handle(
        BoundaryModelSubscribeStudentToCourse command,
        [BoundaryModel]
        SubscriptionState state)
    {
        if (state.StudentId == null)
            throw new InvalidOperationException("Student with given id never enrolled the faculty");

        if (state.CoursesStudentSubscribed >= MaxCoursesPerStudent)
            throw new InvalidOperationException("Student subscribed to too many courses");

        if (state.CourseId == null)
            throw new InvalidOperationException("Course with given id does not exist");

        if (state.StudentsSubscribedToCourse >= state.CourseCapacity)
            throw new InvalidOperationException("Course is fully booked");

        if (state.AlreadySubscribed)
            throw new InvalidOperationException("Student already subscribed to this course");

        return new StudentSubscribedToCourse(FacultyId.Default, command.StudentId, command.CourseId);
    }
}
#endregion


public class Student
{
    public StudentId Id { get; set; }
    
    // Apply() methods here
}

public class Course
{
    public CourseId Id { get; set; }
    
    // Apply() methods here
}

public static class BoundaryModelSubscribeStudent2Handler
{
    public const int MaxCoursesPerStudent = 3;

    public static StudentSubscribedToCourse Handle(
        BoundaryModelSubscribeStudentToCourse command,
        [WriteAggregate]
        Student student, 
        
        [WriteAggregate]
        Course course)
    {
        // pre-conditions

        // This event will be added to both event streams
        return new StudentSubscribedToCourse(FacultyId.Default, command.StudentId, command.CourseId);
    }
}
