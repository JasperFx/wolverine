using JasperFx.Core;
using Wolverine.Transports;

namespace Wolverine.Configuration;

/// <summary>
/// GH-5821. Wolverine identifies an endpoint by its <see cref="Uri" />, and <see cref="Uri" /> lower cases the host.
/// On a transport that carries the endpoint name in the host -- SQS queues, SNS topics, NServiceBus and MassTransit
/// PostgreSQL queues, shared memory topics -- two endpoints whose names differ only in case are two endpoint objects
/// with one identity, and everything keyed on the Uri (the sending agent cache, listener lookups, endpoint policies by
/// Uri) treats them as one. Rather than carry a case-preserving name next to every Uri, Wolverine refuses to start with
/// such a pair.
/// </summary>
internal static class EndpointNameCollisions
{
    /// <summary>
    /// Throw <see cref="EndpointNameCollisionException" /> if any transport whose names are case-sensitive has two
    /// endpoints that share one Uri but were named differently, apart from case.
    /// </summary>
    internal static void AssertNone(IEnumerable<ITransport> transports)
    {
        var problems = transports.SelectMany(Find).ToArray();
        if (problems.Length > 0)
        {
            throw new EndpointNameCollisionException(problems.Join(Environment.NewLine));
        }
    }

    internal static IEnumerable<string> Find(ITransport transport)
    {
        // A transport whose entities are case-insensitive by nature (SQL Server under its default collation, for
        // example) really does mean one entity by Sales and sales, so treating the pair as one is correct
        if (!transport.EndpointNamesAreCaseSensitive)
        {
            yield break;
        }

        var groups = transport.Endpoints()
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<Endpoint>()
            .GroupBy(x => x.Uri);

        foreach (var group in groups)
        {
            // Same identity, but spelled differently -- and the only difference System.Uri forgives is the case of
            // the host. The same name referenced twice is one endpoint object and never gets here
            var spellings = group
                .GroupBy(x => x.Uri.OriginalString, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToArray();

            if (spellings.Length < 2)
            {
                continue;
            }

            var described = spellings.Select(describe).ToArray();
            var names = described.Take(described.Length - 1).Join(", ") + " and " + described.Last();

            yield return
                $"Endpoints {names} on the {transport.Name} transport have names that differ only in case. Wolverine " +
                $"identifies an endpoint by its Uri, and System.Uri lower cases the host, so they would share the one " +
                $"identity '{group.Key}' and be conflated at runtime: messages meant for one could be sent to the other, " +
                "and one endpoint's configuration could be applied to the other. Rename one of them so that the names " +
                "differ by more than case.";
        }
    }

    /// <summary>
    /// The host of a Uri as it was written, with its case, or <see cref="Uri.Host" /> when it cannot be read back from
    /// <see cref="Uri.OriginalString" /> as the same host.
    /// </summary>
    internal static string OriginalHost(Uri uri)
    {
        var original = uri.OriginalString;
        var start = original.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return uri.Host;
        }

        start += 3;
        var end = original.IndexOfAny(['/', '?', '#', ':'], start);
        var host = end < 0 ? original[start..] : original[start..end];

        return string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase) ? host : uri.Host;
    }

    private static string describe(Endpoint endpoint)
    {
        var uri = endpoint.Uri.OriginalString;
        return endpoint.EndpointName == uri || endpoint.EndpointName == endpoint.Uri.ToString()
            ? $"'{uri}'"
            : $"'{endpoint.EndpointName}' ({uri})";
    }
}
