using System.Linq;
using Marten;
using Marten.AspNetCore;
using Wolverine.Http;

namespace WolverineWebApi.Marten;

/// <summary>
/// Endpoints exercising the <see cref="StreamOne{T}"/>, <see cref="StreamMany{T}"/>,
/// <see cref="StreamAggregate{T}"/>, <see cref="StreamPaged{T}"/>, and
/// <see cref="StreamPagedByCursor{T}"/> helpers from <c>Marten.AspNetCore</c>.
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

    // StreamOne with ETag support disabled (opt-out of the default 304 behavior)
    [WolverineGet("/streaming/invoice/{id}/no-etag")]
    public static StreamOne<Invoice> GetOneNoETag(Guid id, IQuerySession session)
        => new(session.Query<Invoice>().Where(x => x.Id == id)) { EmitETag = false };

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

    // StreamPaged - paged JSON envelope (pageNumber, pageSize, totalItemCount, items)
    [WolverineGet("/streaming/invoices/paged")]
    public static StreamPaged<Invoice> GetPaged(int pageNumber, int pageSize, IQuerySession session)
        => new(session.Query<Invoice>().OrderBy(x => x.Id), pageNumber, pageSize);

    // StreamPagedByCursor - keyset pagination with a continuation cursor
    [WolverineGet("/streaming/invoices/cursor")]
    public static StreamPagedByCursor<Invoice> GetByCursor(string? cursor, int pageSize, IQuerySession session)
        => new(session.Query<Invoice>().OrderBy(x => x.Id), cursor, pageSize);
}
