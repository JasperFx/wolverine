using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

internal class DeadLettersTable : Table
{
    public DeadLettersTable(DurabilitySettings durability, string schemaName) : base(
        new SqliteObjectName(DatabaseConstants.DeadLetterTable))
    {
        AddColumn(DatabaseConstants.Id, "TEXT").AsPrimaryKey();
        AddColumn(DatabaseConstants.ExecutionTime, "TEXT");
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "TEXT").NotNull();
        AddColumn(DatabaseConstants.ReceivedAt, "TEXT");
        AddColumn("source", "TEXT");
        AddColumn("explanation", "TEXT");
        AddColumn("exception_text", "TEXT");
        AddColumn("exception_type", "TEXT");
        AddColumn("exception_message", "TEXT");
        AddColumn(DatabaseConstants.SentAt, "TEXT").NotNull().DefaultValueByExpression("(datetime('now'))");
        AddColumn(DatabaseConstants.Replayable, "INTEGER").DefaultValue(1);

        if (durability.DeadLetterQueueExpirationEnabled)
        {
            AddColumn(DatabaseConstants.KeepUntil, "TEXT");
        }
    }
}
