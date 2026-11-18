using Marten;
using Wolverine.Attributes;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Http.FSharpContracts;

// GH-5401: [WaitForProjection<T>] on Marten-backed endpoints. Rendered as F# by HttpFSharpCodegenSample from a host
// with Marten integrated with Wolverine, which the attribute needs.

public record AddPoints(Guid Id, int Points);

public record PointsAdded(int Points);

public class PointsView
{
    public Guid Id { get; set; }
    public int Total { get; set; }
}

public static class ReadYourWritesEndpoints
{
    // Waits after the commit and writes the Wolverine-Consistency-Token response header. Before it runs, it waits
    // for a token on the request.
    [Transactional]
    [WolverinePost("/fsharp/points")]
    [WaitForProjection<PointsView>]
    public static void Post(AddPoints command, IDocumentSession session)
    {
        session.Events.Append(command.Id, new PointsAdded(command.Points));
    }

    // A read only waits for a token on the request: its session appends nothing
    [WolverineGet("/fsharp/points/{id}")]
    [WaitForProjection<PointsView>(TimeoutMs = 2000)]
    public static Task<PointsView?> Get(Guid id, IQuerySession session) => session.LoadAsync<PointsView>(id);
}
