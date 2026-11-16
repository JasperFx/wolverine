using JasperFx.Descriptors;

namespace Wolverine.Runtime.Agents;

/// <summary>
/// Which databases the event-subscription / projection agents of THIS node run against, as a snapshot.
/// Ask it with the database's own <see cref="DatabaseId" /> (the <c>Id</c> of a Weasel / Marten database, or
/// <c>DatabaseDescriptor</c> server + database name), so no caller has to pick which of a database's names to
/// compare. Agent URIs are built from that id; comparing it with a store's own shard identifier (for example
/// Marten's <c>IMartenDatabase.Identifier</c>) silently matches nothing.
/// </summary>
/// <remarks>
/// Build one with <see cref="LocallyOwnedDatabasesExtensions.LocallyOwnedDatabases" />. Under
/// <see cref="DurabilityMode.Solo" /> this node owns every database, which <see cref="IsEvery" /> reports.
/// A snapshot goes stale as Wolverine rebalances agents, so ask the runtime again instead of caching it.
/// Agents of every registered store are mixed together: two stores that share a database both count as owning it.
/// </remarks>
public sealed class LocallyOwnedDatabases
{
    /// <summary>
    /// A node that owns every database, as under <see cref="DurabilityMode.Solo" />.
    /// </summary>
    public static LocallyOwnedDatabases Every { get; } = new(null);

    private readonly IReadOnlySet<DatabaseId>? _ids;

    internal LocallyOwnedDatabases(IReadOnlySet<DatabaseId>? ids) => _ids = ids;

    /// <summary>
    /// True when this node owns every database (Solo), so <see cref="Contains" /> is always true.
    /// </summary>
    public bool IsEvery => _ids is null;

    /// <summary>
    /// Managed distribution is on, but no agent has been assigned to this node (yet).
    /// </summary>
    public bool IsNone => _ids is { Count: 0 };

    /// <summary>
    /// The owned database ids, or an empty list when <see cref="IsEvery" /> because there is nothing to enumerate.
    /// </summary>
    public IReadOnlyCollection<DatabaseId> Ids => _ids is null ? [] : _ids;

    public bool Contains(DatabaseId databaseId)
    {
        ArgumentNullException.ThrowIfNull(databaseId);
        return _ids is null || _ids.Contains(databaseId);
    }
}
