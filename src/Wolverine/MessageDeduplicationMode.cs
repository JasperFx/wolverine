namespace Wolverine;

/// <summary>
/// GH-4757. How logical deduplication ids are compared for equality by the backing store — and
/// therefore what the <c>wolverine_deduplication</c> table looks like.
///
/// <para>
/// This is a three-value enum rather than a boolean because the original design compared the ids as
/// strings, and a string comparison in a relational database is governed by the column's collation,
/// not by the application. On PostgreSQL and SQLite that is a byte comparison and the ids behave as
/// written. On SQL Server and MySQL the default collations are case-insensitive — and MySQL's is
/// accent-insensitive too — so <c>Abc</c> and <c>abc</c> are one claim, as are <c>José</c> and
/// <c>Jose</c>; SQL Server's <c>varchar</c> additionally substitutes <c>?</c> for any character
/// outside its code page, which can collapse two genuinely different ids into one. The result is work
/// wrongly refused as a duplicate, and the same ids behaving differently on different engines.
/// </para>
/// </summary>
public enum MessageDeduplicationMode
{
    /// <summary>
    /// No logical deduplication storage. The <c>wolverine_deduplication</c> table is not provisioned,
    /// <see cref="Persistence.Durability.IMessageStore.Deduplication" /> stays
    /// <c>NullDeduplicationStore</c>, and a <c>[Deduplicated]</c> chain warns at bootstrap and throws
    /// at the first message. The default, so that an upgrade migrates nothing.
    /// </summary>
    None,

    /// <summary>
    /// Compare ids as strings, under whatever collation the database gives
    /// <c>wolverine_deduplication.deduplication_id</c> — which carries the primary key. This is the
    /// original GH-4180 shape, preserved exactly.
    ///
    /// <para>
    /// Choose this to keep the behaviour, and the table, that an application upgrading from before
    /// GH-4757 already has: nothing about the schema or the comparison changes, and in-flight claims
    /// survive the upgrade. The cost is the engine difference above, so it is only the right answer
    /// when the ids in play cannot collide under the local collation — or when a schema change is not
    /// available right now.
    /// </para>
    /// </summary>
    CompareByString,

    /// <summary>
    /// Compare ids by the SHA-256 of their UTF-8 bytes, stored in a binary
    /// <c>deduplication_hash</c> column that carries the primary key. The readable
    /// <c>deduplication_id</c> is stored alongside it as a plain, non-unique column so a stuck claim
    /// can still be identified in the database.
    ///
    /// <para>
    /// Identical on every engine, because a binary column has no collation to apply. What
    /// <c>true</c> on the obsolete <c>EnableMessageDeduplication</c> flag now means.
    /// </para>
    /// </summary>
    CompareByHash
}
