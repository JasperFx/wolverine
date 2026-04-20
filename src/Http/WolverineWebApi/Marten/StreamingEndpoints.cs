using Marten;
using Marten.AspNetCore;
using Wolverine.Http;

namespace WolverineWebApi.Marten;

/// <summary>
/// Endpoints exercising the <see cref="StreamOne{T}"/>, <see cref="StreamMany{T}"/>,
/// and <see cref="StreamAggregate{T}"/> helpers from <c>Marten.AspNetCore</c>.
/// Used by the streaming_endpoints tests for GH-1562. Wolverine.Http dispatches
/// these as ordinary <c>IResult</c> return values via the existing
/// <c>ResultWriterPolicy</c> — no Wolverine-specific code needed.
/// </summary>
public static class StreamingEndpoints
{
    // StreamOne - single document, 404 if not found
    [WolverineGet("/streaming/invoice/{id}")]
    public static StreamOne<Invoice> GetOne(Guid id, IQuerySession session)
        => new(session.Query<Invoice>().Where(x => x.Id == id));

    // StreamOne with custom OnFoundStatus
    [WolverineGet("/streaming/invoice/{id}/custom-status")]
    public static StreamOne<Invoice> GetOneCreated(Guid id, IQuerySession session)
        => new(session.Query<Invoice>().Where(x => x.Id == id))
        {
            OnFoundStatus = StatusCodes.Status202Accepted
        };

    // StreamOne with custom content type
    [WolverineGet("/streaming/invoice/{id}/custom-content-type")]
    public static StreamOne<Invoice> GetOneVendorType(Guid id, IQuerySession session)
        => new(session.Query<Invoice>().Where(x => x.Id == id))
        {
            ContentType = "application/vnd.wolverine.invoice+json"
        };

    // StreamMany - JSON array
    [WolverineGet("/streaming/invoices/approved")]
    public static StreamMany<Invoice> GetApproved(IQuerySession session)
        => new(session.Query<Invoice>().Where(x => x.Approved));

    // StreamMany with no matches - returns empty array, not 404
    [WolverineGet("/streaming/invoices/none")]
    public static StreamMany<Invoice> GetNone(IQuerySession session)
        => new(session.Query<Invoice>().Where(x => x.Id == Guid.Empty));

    // StreamAggregate - event-sourced aggregate, latest state
    [WolverineGet("/streaming/order/{id}")]
    public static StreamAggregate<Order> GetOrder(Guid id, IDocumentSession session)
        => new(session, id);
}
