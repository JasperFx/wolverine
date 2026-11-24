using System.ComponentModel;
using Fisher;
using Fisher.Exceptions;
using JasperFx.Events;

namespace Wolverine.Fisher;

/// <summary>
///     Called by Wolverine's generated code for a load by natural key in the aggregate handler workflow. Not meant to be
///     called directly.
/// </summary>
/// <remarks>
///     GH-5948. Marten's <c>FetchForWriting&lt;T, TKey&gt;</c> answers a key that no stream has with a new, empty stream,
///     so the workflow's not-found check stops the handler (a 404 over HTTP), and a handler that runs anyway starts a new
///     stream. Fisher's throws an <see cref="UnknownNaturalKeyException" /> instead. This gives the workflow Marten's
///     answer on Fisher.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class FisherNaturalKeyLoads
{
    public static async Task<IEventStream<T>> FetchForWriting<T, TKey>(IDocumentSession session, TKey key,
        bool exclusive, bool streamKeys, CancellationToken token) where T : class where TKey : notnull
    {
        try
        {
            return exclusive
                ? await session.Events.FetchForExclusiveWriting<T, TKey>(key, token).ConfigureAwait(false)
                : await session.Events.FetchForWriting<T, TKey>(key, token).ConfigureAwait(false);
        }
        catch (UnknownNaturalKeyException)
        {
            return await newStreamAsync<T>(session, streamKeys, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     With the command's expected version, checked on load: a stale version throws a <c>ConcurrencyException</c>.
    ///     An exclusive load takes no version, as on Marten and Polecat.
    /// </summary>
    public static async Task<IEventStream<T>> FetchForWriting<T, TKey>(IDocumentSession session, TKey key,
        long expectedVersion, bool exclusive, bool streamKeys, CancellationToken token)
        where T : class where TKey : notnull
    {
        try
        {
            return await session.Events.FetchForWriting<T, TKey>(key, expectedVersion, token).ConfigureAwait(false);
        }
        catch (UnknownNaturalKeyException)
        {
            return await newStreamAsync<T>(session, streamKeys, token).ConfigureAwait(false);
        }
    }

    // A new stream with nothing in it, as Marten makes for an unknown key
    private static async Task<IEventStream<T>> newStreamAsync<T>(IDocumentSession session, bool streamKeys,
        CancellationToken token) where T : class
        => streamKeys
            ? await session.Events.FetchForWriting<T>(Guid.NewGuid().ToString(), token).ConfigureAwait(false)
            : await session.Events.FetchForWriting<T>(Guid.NewGuid(), token).ConfigureAwait(false);
}
