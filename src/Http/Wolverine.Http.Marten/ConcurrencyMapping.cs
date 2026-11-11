using Marten.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http.Runtime;

namespace Wolverine.Http.Marten;

/// <summary>
/// GH-4512. The half of the conflict mapping that Wolverine.Http cannot express on its own:
/// <see cref="StreamLockedException"/> -- what <c>FetchForExclusiveWriting</c> throws on a contended stream --
/// derives from <c>MartenException</c> rather than from <c>JasperFx.ConcurrencyException</c>, so the core
/// handler does not see it, and the type is not referenceable from Wolverine.Http.
///
/// <para>
/// GH-5142: the core mapping now catches <c>JasperFx.Events.StreamLockedException</c>, which Polecat's and
/// Fisher's lock exceptions derive from. Marten's does not yet, so this middleware is still registered for
/// Marten, and only while that stays true.
/// </para>
///
/// <para>
/// This is the exact trap the documentation already warns about: a recipe that catches only
/// <c>ConcurrencyException</c> silently leaves the exclusive locking path returning 500s.
/// </para>
/// </summary>
public static class MartenStreamLockedMiddleware
{
    public static ProblemDetails OnException(StreamLockedException ex, WolverineHttpOptions options)
    {
        return ConflictMapping.ToProblemDetails(ex, options);
    }
}
