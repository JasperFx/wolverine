using Microsoft.AspNetCore.Mvc;
using Wolverine.Http.Runtime;

namespace Wolverine.Http.Polecat;

public static class PolecatWolverineHttpOptionsExtensions
{
    /// <summary>
    /// GH-4512. Map Polecat's commit-time concurrency failures onto a 409 ProblemDetails response instead of
    /// letting them escape as an unhandled 500, and advertise the 409 in the OpenAPI document.
    ///
    /// <para>
    /// GH-5459: Polecat throws the shared JasperFx.Events exception types, so this is the core
    /// <c>MapConcurrencyFailuresToConflict()</c> under the Polecat name: everything deriving from
    /// <c>JasperFx.ConcurrencyException</c> (including <c>StreamLockedException</c>) and
    /// <c>ExistingStreamIdCollisionException</c>.
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
