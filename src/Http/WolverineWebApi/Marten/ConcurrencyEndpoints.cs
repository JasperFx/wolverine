using JasperFx;
using Marten;
using Marten.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi.Marten;

// GH-3764. A small aggregate of its own so the concurrency endpoints cannot disturb, or be
// disturbed by, the Order battery that most of the other Marten HTTP tests share.
public record SeatHoldStarted(string Name);

public record SeatHoldConfirmed;

public class SeatHold
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public int Confirmations { get; set; }

    public void Apply(SeatHoldStarted e)
    {
        Name = e.Name;
    }

    public void Apply(SeatHoldConfirmed _)
    {
        Confirmations++;
    }
}

#region sample_marten_concurrency_exception_middleware

/// <summary>
/// Maps Marten's commit time concurrency failures onto a 409 ProblemDetails response
/// instead of letting them escape as an unhandled 500
/// </summary>
public static class MartenConcurrencyExceptionMiddleware
{
    // Marten's optimistic concurrency failures -- EventStreamUnexpectedMaxEventIdException from
    // the event store, and document level concurrency violations -- all derive from
    // JasperFx.ConcurrencyException, so one handler covers them
    public static ProblemDetails OnException(ConcurrencyException ex)
    {
        return new ProblemDetails
        {
            Status = 409,
            Title = "Conflict",
            Detail = ex.Message
        };
    }

    // StreamLockedException does NOT derive from ConcurrencyException -- it is a MartenException --
    // so the FetchForExclusiveWriting path needs its own handler. Catching only ConcurrencyException
    // silently misses it
    public static ProblemDetails OnException(StreamLockedException ex)
    {
        return new ProblemDetails
        {
            Status = 409,
            Title = "Conflict",
            Detail = ex.Message
        };
    }
}

#endregion

public static class ConcurrencyEndpoints
{
    [WolverinePost("/seatholds/create")]
    public static async Task<Guid> Create(IDocumentSession session)
    {
        var id = session.Events.StartStream<SeatHold>(new SeatHoldStarted("Table for two")).Id;
        await session.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// Asserts a stale expected version on purpose, so SaveChangesAsync throws
    /// EventStreamUnexpectedMaxEventIdException -- a JasperFx.ConcurrencyException
    /// </summary>
    [WolverinePost("/seatholds/{id}/optimistic-conflict")]
    public static async Task<string> OptimisticConflict(Guid id, IDocumentSession session)
    {
        // The stream already has one event, so appending a second one lands it at version 2.
        // Claiming 1 is the losing side of an optimistic concurrency race
        session.Events.Append(id, 1, new SeatHoldConfirmed());
        await session.SaveChangesAsync();

        return "no conflict";
    }

    /// <summary>
    /// Takes the exclusive lock the same way a real endpoint would. If something else already
    /// holds it, Marten throws StreamLockedException rather than waiting
    /// </summary>
    [WolverinePost("/seatholds/{id}/exclusive")]
    public static async Task<string> Exclusive(Guid id, IDocumentStore store)
    {
        // The exclusive lock waits on the database rather than failing instantly, so it surfaces as
        // StreamLockedException only once the command times out -- 30 seconds by default, which no
        // HTTP request should ever spend. A short timeout turns a contended stream into a prompt 409
        await using var session = store.LightweightSession(new global::Marten.Services.SessionOptions { Timeout = 2 });
        var stream = await session.Events.FetchForExclusiveWriting<SeatHold>(id);
        stream.AppendOne(new SeatHoldConfirmed());
        await session.SaveChangesAsync();

        return "locked and written";
    }
}
