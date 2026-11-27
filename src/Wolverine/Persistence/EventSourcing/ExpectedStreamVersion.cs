using System.ComponentModel;
using JasperFx;
using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     The expected version check of the aggregate handler workflow on an HTTP endpoint, called by the generated code
///     once the stream is loaded and a missing aggregate has been dealt with. Not for application code.
/// </summary>
/// <remarks>
///     The stream is loaded with <c>FetchForWriting(id)</c> rather than <c>FetchForWriting(id, version)</c>, because a
///     store compares the version inside that call and throws a <see cref="ConcurrencyException" /> for a stream that
///     does not exist before the endpoint can answer that the aggregate is missing. RFC 9110 §13.2.1 has a request that
///     would fail without its precondition fail for that reason, so a missing stream answers 404 (or what the
///     endpoint's <see cref="OnMissing" /> asks for) and only an existing stream at another version answers as a
///     concurrency failure. The store still refuses the append at commit if the stream moves after the load, as it
///     does for any <c>FetchForWriting</c>. A message handler keeps the version in the load.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ExpectedStreamVersion
{
    /// <summary>
    ///     Throw the <see cref="ConcurrencyException" /> the store throws from <c>FetchForWriting(id, version)</c> when
    ///     <paramref name="stream" /> was not at <paramref name="expectedVersion" /> when it was loaded.
    /// </summary>
    public static void AssertStartingVersion<T>(IEventStream<T> stream, long expectedVersion) where T : notnull
    {
        // A stream that did not exist was at version 0, which is what Marten reports in this message too
        var actualVersion = stream.StartingVersion ?? 0;
        if (actualVersion == expectedVersion) return;

        throw new ConcurrencyException(
            $"Expected the existing version to be {expectedVersion}, but was {actualVersion}",
            typeof(T), stream.Key ?? (object)stream.Id);
    }
}
