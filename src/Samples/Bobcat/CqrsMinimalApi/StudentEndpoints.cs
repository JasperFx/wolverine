using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;
using Wolverine.Persistence;

namespace CqrsMinimalApi;

// --- Request/Response records ---

public record CreateStudentRequest(string Name, string? Email, string? Address = null, DateTime? DateOfBirth = null, bool Active = true);
public record CreateStudentResponse(Guid Id);
public record UpdateStudentRequest(string Name, string? Email = null, string? Address = null, DateTime? DateOfBirth = null, bool Active = true);

// --- RESTful Wolverine HTTP endpoints ---
//
// URL shape (`/api/students`, `/api/students/{id}`) and Guid keys are the
// contract the Bobcat spec describes. Status codes follow REST conventions:
//   POST /api/students      -> 201 Created with { Id }
//   GET  /api/students      -> 200 OK with array
//   GET  /api/students/{id} -> 200 OK or 404
//   PUT  /api/students/{id} -> 200 OK or 404
//   POST /api/students/{id} -> 200 OK or 404 (compatibility alias for clients
//                              that don't issue PUT)
//   DELETE /api/students/{id} -> 204 No Content or 404
public static class StudentEndpoints
{
    [WolverinePost("/api/students")]
    public static Created<CreateStudentResponse> Create(
        CreateStudentRequest request,
        IDocumentSession session)
    {
        var student = new Student
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Address = request.Address,
            Email = request.Email,
            DateOfBirth = request.DateOfBirth,
            Active = request.Active
        };

        session.Store(student);

        // `Created<T>` from Microsoft.AspNetCore.Http.HttpResults is a strong-
        // typed result that carries both the 201 status AND the JSON body, so
        // Wolverine.HTTP doesn't have to choose between "this is a cascaded
        // message" and "this is the response". (An earlier
        // (CreateStudentResponse, IResult) tuple return was misinterpreted as
        // body+message, which surfaced as "No routes can be determined for
        // Envelope ... Created<CreateStudentResponse>".)
        var response = new CreateStudentResponse(student.Id);
        return TypedResults.Created($"/api/students/{student.Id}", response);
    }

    [WolverineGet("/api/students")]
    public static async Task<IReadOnlyList<Student>> GetAll(
        IQuerySession session,
        CancellationToken ct)
    {
        return await session.Query<Student>()
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    // [Entity(Required = true)] makes Wolverine.HTTP return 404 automatically
    // when the document isn't present, with no extra branching here.
    [WolverineGet("/api/students/{id}")]
    public static Student GetById(Guid id, [Entity(Required = true)] Student student) => student;

    [WolverinePut("/api/students/{id}")]
    public static Student Update(
        UpdateStudentRequest request,
        Guid id,
        [Entity(Required = true)] Student student,
        IDocumentSession session)
    {
        student.Name = request.Name;
        student.Address = request.Address;
        student.Email = request.Email;
        student.DateOfBirth = request.DateOfBirth;
        student.Active = request.Active;

        session.Store(student);
        return student;
    }

    // POST alias for Update — accepts the same body and route as PUT so
    // clients that prefer POST (or ones that route through proxies that strip
    // PUT) can still update. Both verbs share the implementation.
    [WolverinePost("/api/students/{id}")]
    public static Student UpdateViaPost(
        UpdateStudentRequest request,
        Guid id,
        [Entity(Required = true)] Student student,
        IDocumentSession session) => Update(request, id, student, session);

    [WolverineDelete("/api/students/{id}")]
    public static NoContent Delete(
        Guid id,
        [Entity(Required = true)] Student student,
        IDocumentSession session)
    {
        session.Delete(student);
        // Wolverine's transactional middleware persists the delete; we just
        // need to advertise 204 No Content per REST convention.
        return TypedResults.NoContent();
    }
}
