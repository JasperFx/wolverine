using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Transport;

internal class ScheduledMessageTable : Table
{
    public ScheduledMessageTable(SqliteTransport parent, string tableName) : base(
        new SqliteObjectName(tableName))
    {
        // Turning deterministic ordering off again must never drop the index it added
        AddOnlyMigrations = true;

        AddColumn(DatabaseConstants.Id, "TEXT").AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "TEXT").NotNull();
        AddColumn(DatabaseConstants.ExecutionTime, "TEXT").NotNull();
        AddColumn("timestamp", "TEXT").DefaultValueByExpression("(datetime('now'))");

        if (parent.EnableDeterministicQueueOrdering)
        {
            // Serves the promotion's ORDER BY execution_time, rowid (see QueueTable.QueueOrderColumn)
            Indexes.Add(new IndexDefinition($"idx_{tableName}_execution_time")
            {
                Columns = [DatabaseConstants.ExecutionTime]
            });
        }
    }
}
