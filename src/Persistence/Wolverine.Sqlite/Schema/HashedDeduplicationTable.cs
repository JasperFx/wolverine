using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

/// <summary>
/// GH-4757. Storage for logical message deduplication claims under
/// <see cref="MessageDeduplicationMode.CompareByHash" />. See the PostgreSQL twin for why this is its
/// own table rather than a reshaping of <see cref="DeduplicationTable" />.
/// </summary>
/// <remarks>
/// SQLite never had the defect — a <c>TEXT</c> column's default collation is BINARY, so
/// <see cref="DeduplicationTable" /> already compares byte for byte here. This shape is provisioned
/// anyway so an application's deduplication ids mean the same thing on every engine it might be
/// deployed against, and so a Fisher-backed suite exercises the same code path as its SQL Server twin.
/// SQLite is also the engine that makes reshaping in place impossible: changing a primary key there is
/// the twelve-step table rebuild.
/// </remarks>
internal class HashedDeduplicationTable : Table
{
    public HashedDeduplicationTable(string schemaName) : base(
        new SqliteObjectName(
            TablePrefixing.Apply(schemaName, DatabaseConstants.HashedDeduplicationTableName)))
    {
        // GH-3943: a SQLite "schema" is a table-name prefix, not a namespace, hence TablePrefixing above.

        // BLOB affinity, so the 32 bytes go in and come back unconverted and are compared with memcmp.
        AddColumn(DatabaseConstants.DeduplicationHash, "BLOB").NotNull().AsPrimaryKey();

        // Diagnosis only, and therefore NOT unique. See the PostgreSQL twin.
        AddColumn(DatabaseConstants.DeduplicationId, "TEXT").NotNull();

        AddColumn(DatabaseConstants.Expires, "TEXT").NotNull();
    }
}
