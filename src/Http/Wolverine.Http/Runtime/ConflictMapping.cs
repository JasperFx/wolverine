using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Middleware;

namespace Wolverine.Http.Runtime;

/// <summary>
/// GH-4512. Maps the Critter Stack's commit-time concurrency failures onto a 409 ProblemDetails response
/// instead of letting them escape as an unhandled 500.
///
/// <para>
/// Every optimistic concurrency failure in the stack derives from <see cref="ConcurrencyException"/> --
/// <c>EventStreamUnexpectedMaxEventIdException</c> from the event store, document revision/version
/// violations, <c>DcbConcurrencyException</c>, and <c>SagaConcurrencyException</c> -- so this one handler
/// covers all of them.
/// </para>
///
/// <para>
/// It deliberately does <b>not</b> cover the store-specific exclusive-lock types, because
/// <c>Marten.Exceptions.StreamLockedException</c> and <c>Polecat.Exceptions.StreamLockedException</c>
/// derive from their own store's base exception rather than from <see cref="ConcurrencyException"/>, and
/// neither type is referenceable from Wolverine.Http. Those are added by
/// <c>MapMartenConcurrencyFailuresToConflict()</c> and <c>MapPolecatConcurrencyFailuresToConflict()</c> in
/// the store integration packages, which is why a Marten or Polecat application should call one of those
/// rather than this one directly.
/// </para>
/// </summary>
public static class ConcurrencyExceptionMiddleware
{
    public static ProblemDetails OnException(ConcurrencyException ex, WolverineHttpOptions options)
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
/// GH-4512. The one place a conflict ProblemDetails is shaped, so the core middleware and the store-specific
/// ones registered from Wolverine.Http.Marten / Wolverine.Http.Polecat cannot drift apart.
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
/// GH-4512. Applies the conflict middleware and stamps <c>ProducesProblem(409)</c> on the same chains, so the
/// OpenAPI document advertises the conflict and generated clients know to expect it. Previously a user
/// following the documented recipe had to hand-write this policy as a fourth separate piece.
/// </summary>
/// <remarks>
/// GH-5985. The middleware is applied here rather than through <c>WolverineHttpOptions.AddMiddleware</c>. Those
/// filters run before the chain policies, and <c>AutoApplyTransactions</c> and the store's operation policies are
/// chain policies that only then mark a chain transactional, so the default filter,
/// <see cref="ConflictMapping.IsTransactional" />, missed every chain they made transactional: a 500 for a
/// concurrency failure, with a 409 advertised. An <see cref="IHttpPolicy" /> runs after the chain policies, so the
/// middleware and the OpenAPI metadata see the same settled chain.
/// </remarks>
public class ConflictProblemPolicy : IHttpPolicy
{
    private readonly Func<HttpChain, bool> _filter;

    public ConflictProblemPolicy(Func<HttpChain, bool> filter)
    {
        _filter = filter;
    }

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var matching = chains.Where(_filter).ToArray();
        if (matching.Length == 0) return;

        // The conflict middleware is only OnException handlers, which wrap the whole chain wherever it is applied
        var middleware = new MiddlewarePolicy();
        middleware.AddType(typeof(ConcurrencyExceptionMiddleware));
        middleware.Apply(matching, rules, container);

        foreach (var chain in matching)
        {
            chain.Metadata.ProducesProblem(ConflictMapping.ConflictStatusCode);
        }
    }
}
