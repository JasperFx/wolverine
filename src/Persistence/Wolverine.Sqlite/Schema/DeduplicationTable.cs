using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

/// <summary>
/// GH-4180. Storage for logical message deduplication claims. See the PostgreSQL twin for why this
/// is its own table rather than a column on the incoming envelope table.
/// </summary>
internal class DeduplicationTable : Table
{
    public DeduplicationTable(string schemaName) : base(
        new SqliteObjectName(TablePrefixing.Apply(schemaName, DatabaseConstants.DeduplicationTableName)))
    {
        // GH-3943: a SQLite "schema" is a table-name prefix, not a namespace, hence TablePrefixing above.
        AddColumn(DatabaseConstants.DeduplicationId, "TEXT").NotNull().AsPrimaryKey();
        AddColumn(DatabaseConstants.Expires, "TEXT").NotNull();
    }
}
