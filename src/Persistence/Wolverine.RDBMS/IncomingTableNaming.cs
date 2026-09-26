namespace Wolverine.RDBMS;

/// <summary>
/// GH-4565. Decides whether a table name a database named in an error message is Wolverine's own
/// inbox table.
/// </summary>
/// <remarks>
/// <para>Used by the provider-specific "is this a duplicate INBOX row?" predicates that back the
/// <c>Discard()</c> rule in <c>PolecatIntegration</c> and <c>FisherIntegration</c>. Those rules ran on the
/// provider's error number alone, so <em>any</em> primary-key or unique-key violation anywhere in the
/// handler's transaction — a duplicate natural key, a unique index on the application's own table — was
/// acknowledged and dropped. Not retried, not dead-lettered: the work never happened and there was no dead
/// letter to find it in.</para>
///
/// <para>Matched on the trailing segment rather than the whole name because a table name may carry a
/// schema prefix (<see cref="TablePrefixing"/>, for databases with no schemas) or a schema qualifier, and
/// the schema is chosen per application.</para>
/// </remarks>
public static class IncomingTableNaming
{
    /// <summary>
    /// Whether <paramref name="tableName"/> — possibly schema-qualified, possibly prefixed — names the
    /// incoming envelopes table.
    /// </summary>
    public static bool IsIncomingTable(string? tableName)
    {
        return names(tableName, DatabaseConstants.IncomingTable);
    }

    /// <summary>
    /// GH-4571. Whether <paramref name="tableName"/> — possibly schema-qualified, possibly prefixed —
    /// names the logical deduplication table. Same reasoning as <see cref="IsIncomingTable"/>: the
    /// commit-race classifier behind a transactional deduplication claim has to tell its own primary key
    /// violation apart from one raised by the application's own tables in the same transaction.
    /// </summary>
    public static bool IsDeduplicationTable(string? tableName)
    {
        return names(tableName, DatabaseConstants.DeduplicationTableName);
    }

    private static bool names(string? tableName, string bareTableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return false;

        // Strip a schema qualifier ("wolverine.wolverine_incoming_envelopes") and any quoting the
        // provider put around the parts
        var bare = tableName.Split('.').Last().Trim('[', ']', '"', '`', '\'', ' ');

        // An exact match covers the unprefixed table; EndsWith covers TablePrefixing's "{schema}_" form
        return bare == bareTableName
               || bare.EndsWith($"_{bareTableName}", StringComparison.Ordinal);
    }
}
