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
    /// <see cref="global::Marten.Exceptions.StreamLockedException"/>, which <c>FetchForExclusiveWriting</c>
    /// throws on a contended stream and which does <b>not</b> derive from <c>ConcurrencyException</c>.
    /// Catching only the former silently leaves the exclusive locking path returning 500s.
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
    public static void MapMartenConcurrencyFailuresToConflict(this WolverineHttpOptions options,
        Func<HttpChain, bool>? filter = null)
    {
        filter ??= ConflictMapping.IsTransactional;

        options.MapConcurrencyFailuresToConflict(filter);
        options.AddMiddleware(typeof(MartenStreamLockedMiddleware), filter);
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