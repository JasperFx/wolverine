using System.Security.Cryptography;
using System.Text;

namespace Wolverine.MySql.Transport;

/// <summary>
///     MySQL refuses table, index and other identifiers longer than 64 characters, and the queue
///     transport derives its names from the queue name. Same idea as <c>PostgresqlIdentifier.Shorten</c>.
/// </summary>
internal static class MySqlIdentifier
{
    internal const int MaxLength = 64;

    /// <summary>
    ///     Returns <paramref name="name" /> unchanged when it fits, so no existing table or index is ever
    ///     renamed. A longer name is truncated with a deterministic hash of the whole name, so the model
    ///     always matches what MySQL stores and two long names that share a prefix stay distinct.
    /// </summary>
    internal static string Shorten(string name)
    {
        if (name.Length <= MaxLength) return name;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        return $"{name[..(MaxLength - hash.Length - 1)]}_{hash}";
    }
}
