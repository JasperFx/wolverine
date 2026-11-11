using JasperFx;
using Marten;
using JasperFx.Events;
using Marten.Schema;
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

public record SeatNoteIds(Guid[] Ids);

// GH-5355. Documents under optimistic concurrency, so one commit can fail more than one version check
[UseOptimisticConcurrency]
public class SeatNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = null!;
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

    // Before Marten 10, StreamLockedException did NOT derive from ConcurrencyException (it was a MartenException);
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

    /// <summary>
    /// GH-5142. Throws the store-agnostic lock failure that Polecat's and Fisher's StreamLockedException
    /// derive from. Marten's own type does not, so only the core conflict mapping can catch this one
    /// </summary>
    [WolverinePost("/seatholds/{id}/store-agnostic-lock")]
    public static string StoreAgnosticLock(Guid id)
    {
        throw new global::JasperFx.Events.StreamLockedException(id, null);
    }

    [WolverinePost("/seatnotes/create")]
    public static async Task<Guid[]> CreateNotes(IDocumentSession session)
    {
        var notes = new[] { new SeatNote { Id = Guid.NewGuid(), Text = "a" }, new SeatNote { Id = Guid.NewGuid(), Text = "b" } };
        session.Store(notes);
        await session.SaveChangesAsync();
        return notes.Select(x => x.Id).ToArray();
    }

    /// <summary>
    /// GH-5355. Loads two optimistic concurrency documents, lets another session change both underneath
    /// this one, then saves -- one commit failing two version checks, so Marten throws an AggregateException
    /// of ConcurrencyExceptions
    /// </summary>
    [WolverinePost("/seatnotes/aggregate-conflict")]
    public static async Task<string> AggregateConflict(SeatNoteIds command, IDocumentSession session, IDocumentStore store)
    {
        var ids = command.Ids;
        var notes = await session.LoadManyAsync<SeatNote>(ids);

        await using (var other = store.LightweightSession())
        {
            foreach (var note in await other.LoadManyAsync<SeatNote>(ids))
            {
                note.Text = "changed elsewhere";
                other.Store(note);
            }

            await other.SaveChangesAsync();
        }

        foreach (var note in notes)
        {
            note.Text = "stale";
            session.Store(note);
        }

        await session.SaveChangesAsync();
        return "no conflict";
    }

    /// <summary>
    /// GH-5355. An aggregate that is NOT all concurrency failures must not be claimed by the 409 mapping
    /// </summary>
    [WolverinePost("/seatnotes/mixed-aggregate")]
    public static string MixedAggregate()
    {
        throw new AggregateException(new ConcurrencyException("lost"), new InvalidOperationException("bug"));
    }
}
