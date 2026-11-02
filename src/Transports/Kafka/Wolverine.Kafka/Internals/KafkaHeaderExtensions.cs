using Dekaf.Consumer;
using Dekaf.Serialization;

namespace Wolverine.Kafka.Internals;

internal static class KafkaHeaderExtensions
{
    /// <summary>
    /// Reads the header named <paramref name="key" />, where the last occurrence of a repeated key wins. The value
    /// is copied out of the consumer's pooled buffer, so it stays valid after the consumer advances.
    /// </summary>
    /// <param name="skipNullValues">Pass over occurrences whose value is null instead of returning them</param>
    public static bool TryGetLastHeaderValue(this ConsumeResult<string, byte[]> incoming, string key,
        bool skipNullValues, out string? value)
    {
        return incoming.Headers.TryGetLastHeaderValue(key, skipNullValues, out value);
    }

    /// <summary>
    /// Reads the header named <paramref name="key" /> from headers already read off a record. Every read of
    /// <c>ConsumeResult.Headers</c> copies all of the record's headers, so read them once and look up from that.
    /// </summary>
    public static bool TryGetLastHeaderValue(this IReadOnlyList<Header> headers, string key, bool skipNullValues,
        out string? value)
    {
        for (var i = headers.Count - 1; i >= 0; i--)
        {
            var header = headers[i];
            if (header.Key != key || (skipNullValues && header.IsValueNull)) continue;

            value = header.IsValueNull ? null : header.GetValueAsString();
            return true;
        }

        value = null;
        return false;
    }
}
