using Microsoft.AspNetCore.Mvc;
using Polecat.Exceptions;
using Wolverine.Http.Runtime;

namespace Wolverine.Http.Polecat;

/// <summary>
/// GH-4512. The Polecat half of the conflict mapping, for the same reason as Marten's:
/// <see cref="StreamLockedException"/> -- what <c>FetchForExclusiveWriting</c> throws on a contended stream --
/// does not derive from <c>JasperFx.ConcurrencyException</c>, so the core handler does not see it, and the
/// type is not referenceable from Wolverine.Http.
///
/// <para>
/// Fisher needs no equivalent: its exclusive methods are optimistic and throw
/// <c>EventStreamUnexpectedMaxEventIdException</c>, which already derives from
/// <c>JasperFx.ConcurrencyException</c> and is covered by the core mapping.
/// </para>
/// </summary>
public static class PolecatStreamLockedMiddleware
{
    public static ProblemDetails OnException(StreamLockedException ex)
    {
        return ConflictMapping.ToProblemDetails(ex);
    }
}

public static class PolecatWolverineHttpOptionsExtensions
{
    /// <summary>
    /// GH-4512. Map Polecat's commit-time concurrency failures onto a 409 ProblemDetails response instead of
    /// letting them escape as an unhandled 500, and advertise the 409 in the OpenAPI document.
    ///
    /// <para>
    /// This is the call a Polecat application wants, because it covers <b>both</b> everything deriving from
    /// <c>JasperFx.ConcurrencyException</c> <b>and</b> <see cref="StreamLockedException"/>, which does not.
    /// </para>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="filter">
    /// Which chains the mapping applies to. Defaults to the transactional chains.
    /// </param>
    public static void MapPolecatConcurrencyFailuresToConflict(this WolverineHttpOptions options,
        Func<HttpChain, bool>? filter = null)
    {
        filter ??= ConflictMapping.IsTransactional;

        options.MapConcurrencyFailuresToConflict(filter);
        options.AddMiddleware(typeof(PolecatStreamLockedMiddleware), filter);
    }
}
