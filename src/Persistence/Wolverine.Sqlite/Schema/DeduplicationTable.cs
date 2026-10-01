using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

/// <summary>
/// GH-4180. Storage for logical message deduplication claims. See the PostgreSQL twin for why this
/// is its own table rather than a column on the incoming envelope table, and for what the two
/// <see cref="MessageDeduplicationMode" /> shapes are.
/// </summary>
/// <remarks>
/// GH-4757. SQLite never had the defect — a <c>TEXT</c> column's default collation is BINARY, so the
/// string mode already compares byte for byte here. The hash shape is provisioned anyway so that an
/// application's deduplication ids mean the same thing on every engine it might be deployed against,
/// and so that a Fisher-backed test suite exercises the same code path as its SQL Server twin.
/// </remarks>
internal class DeduplicationTable : Table
{
    public DeduplicationTable(string schemaName, MessageDeduplicationMode mode) : base(
        new SqliteObjectName(TablePrefixing.Apply(schemaName, DatabaseConstants.DeduplicationTableName)))
    {
        // GH-3943: a SQLite "schema" is a table-name prefix, not a namespace, hence TablePrefixing above.
        if (mode == MessageDeduplicationMode.CompareByHash)
        {
            // BLOB affinity, so the 32 bytes go in and come back unconverted and are compared with
            // memcmp.
            AddColumn(DatabaseConstants.DeduplicationHash, "BLOB").NotNull().AsPrimaryKey();

            // Diagnosis only, and therefore NOT unique. See the PostgreSQL twin.
            AddColumn(DatabaseConstants.DeduplicationId, "TEXT").NotNull();
        }
        else
        {
            AddColumn(DatabaseConstants.DeduplicationId, "TEXT").NotNull().AsPrimaryKey();
        }

        AddColumn(DatabaseConstants.Expires, "TEXT").NotNull();
    }
}
