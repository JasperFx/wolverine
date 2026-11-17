using JasperFx.Descriptors;
using Weasel.Core.Migrations;
using Wolverine.Runtime.Agents;

namespace Wolverine.Polecat;

public static class LocallyOwnedDatabasesPolecatExtensions
{
    /// <summary>
    /// Whether this node runs agents against <paramref name="database" />. Asked with the database itself
    /// (its server and database name, which Wolverine builds agent URIs from), not with the database's
    /// <c>Identifier</c> which never matches an agent URI.
    /// </summary>
    public static bool Contains(this LocallyOwnedDatabases owned, IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(database);

        // Polecat 6.0.0-alpha.1 leaves IDatabase.Id null, so fall back to the descriptor, which carries the
        // same server and database name the agent URIs are built from
        var id = database.Id;
        if (id is null)
        {
            var descriptor = database.Describe();
            id = new DatabaseId(descriptor.ServerName, descriptor.DatabaseName);
        }

        return owned.Contains(id);
    }
}
