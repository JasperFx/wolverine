using Weasel.Core.Migrations;
using Wolverine.Runtime.Agents;

namespace Wolverine.Marten;

public static class LocallyOwnedDatabasesMartenExtensions
{
    /// <summary>
    /// Whether this node runs agents against <paramref name="database" />. Asked with the database itself
    /// (its <see cref="IDatabase.Id" />, which Wolverine builds agent URIs from), not with Marten's shard
    /// <c>Identifier</c> which never matches an agent URI.
    /// </summary>
    public static bool Contains(this LocallyOwnedDatabases owned, IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(database);
        return owned.Contains(database.Id);
    }
}
