using Microsoft.AspNetCore.Mvc;
using Wolverine.Http.Runtime;

namespace Wolverine.Http.Marten;

public static class WolverineHttpOptionsExtensions
{
    /// <summary>
    /// GH-4512. Map Marten's commit-time concurrency failures onto a 409 ProblemDetails response instead of
    /// letting them escape as an unhandled 500, and advertise the 409 in the OpenAPI document.
    ///
    /// <para>
    /// This is the call a Marten application wants, because it covers <b>both</b> halves:
    /// everything deriving from <c>JasperFx.ConcurrencyException</c> (<c>EventStreamUnexpectedMaxEventIdException</c>,
    /// document revision/version violations, <c>SagaConcurrencyException</c>) <b>and</b>
    /// <see cref="global::JasperFx.Events.StreamLockedException"/>, which <c>FetchForExclusiveWriting</c>
    /// throws on a contended stream. Before Marten 10 that was <c>Marten.Exceptions.StreamLockedException</c>,
    /// which did <b>not</b> derive from <c>ConcurrencyException</c>, and catching only the former silently left
    /// the exclusive locking path returning 500s. GH-5142: both are now mapped by the core
    /// <c>MapConcurrencyFailuresToConflict()</c>, so this is the same call under the Marten name.
    /// </para>
    ///
    /// <para>
    /// Replaces the four-part recipe in docs/guide/http/exception-handling.md: a middleware class with two
    /// OnException overloads, an AddMiddleware registration, and an IHttpPolicy for the OpenAPI metadata.
    /// </para>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="filter">
    /// Which chains the mapping applies to. Defaults to the transactional chains -- the only ones that commit
    /// a unit of work, and so the only ones that can raise a commit-time concurrency failure.
    /// </param>
    /// <param name="toProblemDetails">
    /// Optional factory that builds the ProblemDetails returned for the exception, replacing the default
    /// (which puts the exception message in <c>Detail</c>). A null <c>Status</c> is filled in as 409.
    /// </param>
    public static void MapMartenConcurrencyFailuresToConflict(this WolverineHttpOptions options,
        Func<HttpChain, bool>? filter = null, Func<Exception, ProblemDetails>? toProblemDetails = null)
    {
        filter ??= ConflictMapping.IsTransactional;

        // GH-5142: the core mapping catches JasperFx.Events.StreamLockedException, which is what Marten 10
        // throws from FetchForExclusiveWriting (marten#5858), so no Marten-specific catch is registered any more.
        options.MapConcurrencyFailuresToConflict(filter, toProblemDetails);
    }

    /// <summary>
    /// Adds an <see cref="IResourceWriterPolicy"/> that streams <see cref="ICompiledQuery"/>
    /// </summary>
    /// <param name="options">Options to apply policy on</param>
    public static void UseMartenCompiledQueryResultPolicy(this WolverineHttpOptions options,
        string responseType = "application/json", int successStatusCode = 200)
    {
        options.AddResourceWriterPolicy(new CompiledQueryWriterPolicy(responseType, successStatusCode));
    }
}