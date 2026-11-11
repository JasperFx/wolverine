using Microsoft.AspNetCore.Mvc;
using JasperFx.Events;
using Wolverine.Http.Runtime;

namespace Wolverine.Http.Polecat;

/// <summary>
/// GH-4512. The Polecat half of the conflict mapping, for the same reason as Marten's:
/// <see cref="StreamLockedException"/> -- what <c>FetchForExclusiveWriting</c> throws on a contended stream.
/// Before Polecat 6 it was <c>Polecat.Exceptions.StreamLockedException</c>, which did not derive from
/// <c>JasperFx.ConcurrencyException</c>, so the core handler did not see it. Polecat 6 throws the shared
/// <c>JasperFx.Events.StreamLockedException</c>, which does; this middleware stays so
/// <c>MapPolecatConcurrencyFailuresToConflict</c> keeps its shape and the lock timeout is mapped explicitly.
///
/// <para>
/// GH-5142: no longer registered. Polecat's <see cref="StreamLockedException"/> derives from
/// <c>JasperFx.Events.StreamLockedException</c>, which the core <c>ConcurrencyExceptionMiddleware</c> now maps
/// itself, for Polecat and Fisher alike. The class stays public so existing references keep compiling;
/// GH-5145 retires it.
/// </para>
/// </summary>
public static class PolecatStreamLockedMiddleware
{
    public static ProblemDetails OnException(StreamLockedException ex, WolverineHttpOptions options)
    {
        return ConflictMapping.ToProblemDetails(ex, options);
    }
}

public static class PolecatWolverineHttpOptionsExtensions
{
    /// <summary>
    /// GH-4512. Map Polecat's commit-time concurrency failures onto a 409 ProblemDetails response instead of
    /// letting them escape as an unhandled 500, and advertise the 409 in the OpenAPI document.
    ///
    /// <para>
    /// Covers everything deriving from <c>JasperFx.ConcurrencyException</c> and <see cref="StreamLockedException"/>,
    /// which does not. GH-5142: both are now mapped by the core <c>MapConcurrencyFailuresToConflict()</c>, so
    /// this is the same call under the Polecat name.
    /// </para>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="filter">
    /// Which chains the mapping applies to. Defaults to the transactional chains.
    /// </param>
    /// <param name="toProblemDetails">
    /// Optional factory that builds the ProblemDetails returned for the exception, replacing the default
    /// (which puts the exception message in <c>Detail</c>). A null <c>Status</c> is filled in as 409.
    /// </param>
    public static void MapPolecatConcurrencyFailuresToConflict(this WolverineHttpOptions options,
        Func<HttpChain, bool>? filter = null, Func<Exception, ProblemDetails>? toProblemDetails = null)
    {
        filter ??= ConflictMapping.IsTransactional;

        options.MapConcurrencyFailuresToConflict(filter, toProblemDetails);
    }
}
