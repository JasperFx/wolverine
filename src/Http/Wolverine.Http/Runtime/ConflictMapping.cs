using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
    public static ProblemDetails OnException(ConcurrencyException ex)
    {
        return ConflictMapping.ToProblemDetails(ex);
    }
}

/// <summary>
/// GH-4512. The one place a conflict ProblemDetails is shaped, so the core middleware and the store-specific
/// ones registered from Wolverine.Http.Marten / Wolverine.Http.Polecat cannot drift apart.
/// </summary>
public static class ConflictMapping
{
    public const int ConflictStatusCode = 409;

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
