using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;

namespace MartenTests.Dcb.University;

public record EnrollStudentInFaculty(StudentId StudentId, string FirstName, string LastName);

/// <summary>
/// Enrolls a student in the faculty (idempotent).
/// Uses DCB to query by StudentId tag.
/// </summary>
public static class EnrollStudentHandler
{
    public static async Task Handle(EnrollStudentInFaculty command, IDocumentSession session)
    {
        var query = new EventTagQuery()
            .Or<StudentEnrolledInFaculty, StudentId>(command.StudentId);
        var boundary = await session.Events.FetchForWritingByTags<EnrolledStudentState>(query);

        if (boundary.Aggregate is { Exists: true })
            return; // Already enrolled, idempotent

        var @event = new StudentEnrolledInFaculty(FacultyId.Default, command.StudentId, command.FirstName, command.LastName);
        boundary.AppendOne(@event);
    }
}
