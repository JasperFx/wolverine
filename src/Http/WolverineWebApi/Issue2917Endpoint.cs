using Microsoft.AspNetCore.Http;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi;

// Endpoint that reproduces https://github.com/JasperFx/wolverine/issues/2917 — a transactional
// EF Core endpoint that returns a cascading message alongside its HTTP response. Under EF Core's
// Eager transaction middleware the cascading Issue2917Message is only sent inside
// EnrollDbContextInTransaction's CommitAsync(), which runs AFTER WriteJsonAsync writes the response.
public record Issue2917Message(string Name);

public record Issue2917Request(string Name);

public static class Issue2917Endpoint
{
    // The ItemsDbContext dependency engages EF Core's Eager transaction middleware (the same as
    // the user's AutoApplyTransactions setup).
    [Transactional]
    [WolverinePost("/issue2917")]
    public static (IResult, OutgoingMessages) Post(Issue2917Request request, ItemsDbContext db)
    {
        return (Results.Ok("ok"), [new Issue2917Message(request.Name)]);
    }
}

public class Issue2917Handler
{
    public void Handle(Issue2917Message message)
    {
        // no-op; we only care that the message is tracked
    }
}
