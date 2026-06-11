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
            // GH-3071 — every other RDBMS backend (Postgres, SqlServer, MySql,
            // Oracle) names this column `expires` (DatabaseConstants.Expires)
            // because that's what the shared DLQ insert path in
            // `DatabasePersistence.WriteDeadLetter` and the cleanup query in
            // `DeleteExpiredDeadLetterMessagesOperation` both reference. The
            // Sqlite schema previously named it `keep_until`
            // (DatabaseConstants.KeepUntil), which provisioned a column the
            // shared SQL never wrote to and failed the cleanup job with
            // `no such column: expires`. Aligning with the rest of the
            // backends closes the schema-vs-SQL gap.
            AddColumn(DatabaseConstants.Expires, "TEXT");
        }
    }
}
