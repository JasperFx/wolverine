using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Transport;

internal class QueueTable : Table
{
    /// <summary>
    ///     Breaks timestamp ties when deterministic queue ordering is enabled. The queue tables
    ///     declare a TEXT primary key, so every row also carries SQLite's implicit rowid, and a new
    ///     row always gets a rowid greater than that of every row still in the table. That is
    ///     insertion order for the rows a pop can see, so no column has to be added for it.
    /// </summary>
    internal const string QueueOrderColumn = "rowid";

    public QueueTable(SqliteTransport parent, string tableName) : base(
        new SqliteObjectName(tableName))
    {
        // Turning deterministic ordering off again must never drop the index it added
        AddOnlyMigrations = true;

        AddColumn(DatabaseConstants.Id, "TEXT").AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "TEXT").NotNull();
        AddColumn(DatabaseConstants.KeepUntil, "TEXT");
        AddColumn("timestamp", "TEXT").DefaultValueByExpression("(datetime('now'))");

        if (parent.EnableDeterministicQueueOrdering)
        {
            // Every SQLite index entry ends with the row's rowid, so this one index serves
            // ORDER BY timestamp, rowid without a sort step
            Indexes.Add(new IndexDefinition($"idx_{tableName}_timestamp") { Columns = ["timestamp"] });
        }
    }
}
