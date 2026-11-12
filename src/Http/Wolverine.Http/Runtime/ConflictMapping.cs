using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.Runtime;

/// <summary>
/// GH-4512. Maps the Critter Stack's commit-time concurrency failures onto a 409 ProblemDetails response
/// instead of letting them escape as an unhandled 500.
///
/// <para>
/// GH-5459: this is the one conflict mapping for Marten, Polecat and Fisher alike, because all three throw the
/// shared JasperFx.Events exception types (or subclasses of them):
/// </para>
/// <list type="bullet">
/// <item>Everything deriving from <see cref="ConcurrencyException"/> -- <c>EventStreamUnexpectedMaxEventIdException</c>
/// from the event store, document revision/version violations, <c>DcbConcurrencyException</c> and
/// <c>SagaConcurrencyException</c>.</item>
/// <item><see cref="StreamLockedException"/>, what <c>FetchForExclusiveWriting</c> throws on a contended stream.
/// As of JasperFx 3.0 it derives from <see cref="ConcurrencyException"/> too; it keeps its own handler so the
/// lock timeout stays mapped explicitly.</item>
/// <item><see cref="ExistingStreamIdCollisionException"/>, what <c>StartStream</c> throws when the stream id is
/// already taken. It is not a <see cref="ConcurrencyException"/>, but it is the same kind of failure from the
/// client's side: the request conflicts with the current state of the resource.</item>
/// </list>
/// </summary>
public static class ConcurrencyExceptionMiddleware
{
    public static ProblemDetails OnException(ConcurrencyException ex, WolverineHttpOptions options)
    {
        return ConflictMapping.ToProblemDetails(ex, options);
    }

    /// <summary>
    /// GH-5142. The store-agnostic exclusive-lock failure. A lost race for the stream lock, so a 409 the
    /// client can retry, exactly like an optimistic concurrency failure. A <see cref="ConcurrencyException"/>
    /// since JasperFx 3.0; generated catch blocks are ordered most-specific first, so the two never collide.
    /// </summary>
    public static ProblemDetails OnException(StreamLockedException ex, WolverineHttpOptions options)
    {
        return ConflictMapping.ToProblemDetails(ex, options);
    }

    /// <summary>
    /// GH-5459. <c>StartStream</c> with an id that already exists, on any of Marten, Polecat or Fisher. The
    /// stream the client asked to create is already there, so a 409 rather than a 500.
    /// </summary>
    public static ProblemDetails OnException(ExistingStreamIdCollisionException ex, WolverineHttpOptions options)
    {
        return ConflictMapping.ToProblemDetails(ex, options);
    }

    /// <summary>
    /// A commit that fails several version checks at once (Marten's <c>SaveChangesAsync</c> with two
    /// <c>[UseOptimisticConcurrency]</c> documents, say) throws an <see cref="AggregateException"/> carrying one
    /// <see cref="ConcurrencyException"/> per failed check. Still a lost race, still the client's to retry.
    /// </summary>
    public static ProblemDetails OnException(AggregateException ex, WolverineHttpOptions options)
    {
        return ConflictMapping.ToProblemDetails(ex, options);
    }

    /// <summary>
    /// Becomes <c>catch (AggregateException ex) when (...)</c>, so an aggregate with anything other than
    /// concurrency failures in it is not swallowed here but falls through to the sibling catch clauses.
    /// </summary>
    public static bool OnExceptionFilter(AggregateException ex)
    {
        return ConflictMapping.IsConflict(ex);
    }
}

/// <summary>
/// GH-4512. The one place a conflict ProblemDetails is shaped.
/// </summary>
public static class ConflictMapping
{
    public const int ConflictStatusCode = 409;

    /// <summary>
    /// True when the aggregate is non-empty and every leaf exception, however deeply nested, is a
    /// <see cref="ConcurrencyException"/>.
    /// </summary>
    public static bool IsConflict(AggregateException ex)
    {
        var inner = ex.Flatten().InnerExceptions;
        return inner.Count > 0 && inner.All(x => x is ConcurrencyException);
    }

    /// <summary>
    /// Shape the conflict response, using the factory registered through
    /// <see cref="WolverineHttpOptions.ConflictProblemDetailsFactory"/> when there is one and the default
    /// otherwise.
    /// </summary>
    public static ProblemDetails ToProblemDetails(Exception ex, WolverineHttpOptions options)
    {
        var problem = options.ConflictProblemDetailsFactory?.Invoke(ex) ?? ToProblemDetails(ex);
        problem.Status ??= ConflictStatusCode;
        return problem;
    }

    public static ProblemDetails ToProblemDetails(Exception ex)
    {
        return new ProblemDetails
        {
            Status = ConflictStatusCode,
            Title = "Conflict",
            Detail = ex.Message
        };
    }

    /// <summary>
    /// The default set of chains this mapping applies to: the ones that actually commit a unit of work, and
    /// so are the only ones that can raise a commit-time concurrency failure in the first place.
    /// </summary>
    public static bool IsTransactional(HttpChain chain) => chain.IsTransactional;
}

/// <summary>
/// GH-4512. Stamps <c>ProducesProblem(409)</c> on the same chains the conflict middleware is applied to, so
/// the OpenAPI document advertises the conflict and generated clients know to expect it. Previously a user
/// following the documented recipe had to hand-write this policy as a fourth separate piece.
/// </summary>
public class ConflictProblemPolicy : IHttpPolicy
{
    private readonly Func<HttpChain, bool> _filter;

    public ConflictProblemPolicy(Func<HttpChain, bool> filter)
    {
        _filter = filter;
    }

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(_filter))
        {
            chain.Metadata.ProducesProblem(ConflictMapping.ConflictStatusCode);
        }
    }
}
