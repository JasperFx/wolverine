using JasperFx.Core;
using Wolverine.Configuration;

namespace Wolverine.RDBMS.Transport;

/// <summary>
///     GH-5814. Finds a database queue from its endpoint <see cref="Uri" /> without losing the case of its name.
///     <see cref="Uri.Host" /> is lowercased by <see cref="System.Uri" />, but a queue table name can be
///     case-sensitive: always on PostgreSQL, where NServiceBus quotes it, and on SQL Server under a case-sensitive
///     collation. Looking a queue up by <c>Uri.Host</c> sent a reply for <c>Sales.Endpoint</c> to <c>sales.endpoint</c>.
/// </summary>
internal static class CaseSensitiveQueueLookup
{
    /// <summary>
    ///     The queue with the name the Uri was built from. An exact match wins. A name without upper-case letters
    ///     may come from a Uri rebuilt from its string form (<see cref="Uri.ToString" /> lowercases the host), so if
    ///     exactly one known queue matches it apart from case, that is the queue it means. Otherwise the queue is
    ///     found or created by the name as given.
    /// </summary>
    internal static TQueue Find<TQueue>(LightweightCache<string, TQueue> queues, Uri uri, Func<TQueue, string> nameOf)
        where TQueue : notnull
    {
        var name = NameFor(uri);
        if (queues.TryFind(name, out var queue))
        {
            return queue;
        }

        if (!name.Any(char.IsUpper))
        {
            var matches = queues.Where(x => string.Equals(nameOf(x), name, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }
        }

        return queues[name];
    }

    /// <summary>
    ///     The queue name a <c>{protocol}://{name}</c> Uri carries, with its case: read from
    ///     <see cref="Uri.OriginalString" />, or from the path of the <c>{protocol}://queue/{escaped name}</c> form used
    ///     for a name that is not a valid Uri host.
    /// </summary>
    internal static string NameFor(Uri uri)
    {
        if (uri.Host == "queue" && uri.AbsolutePath.Length > 1)
        {
            return Uri.UnescapeDataString(uri.AbsolutePath[1..]);
        }

        // GH-5821's reading of the host as it was written, with its case
        return EndpointNameCollisions.OriginalHost(uri);
    }
}
