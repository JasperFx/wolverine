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
        AddColumn(DatabaseConstants.Id, "TEXT").AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "TEXT").NotNull();
        AddColumn(DatabaseConstants.ExecutionTime, "TEXT").NotNull();
        AddColumn("timestamp", "TEXT").DefaultValueByExpression("(datetime('now'))");
    }
}
