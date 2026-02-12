using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

internal class IncomingEnvelopeTable : Table
{
    public IncomingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new SqliteObjectName(DatabaseConstants.IncomingTable))
    {
        AddColumn(DatabaseConstants.Id, "TEXT").AsPrimaryKey();
        AddColumn(DatabaseConstants.Status, "TEXT").NotNull();
        AddColumn(DatabaseConstants.OwnerId, "INTEGER").NotNull();
        AddColumn(DatabaseConstants.ExecutionTime, "TEXT");
        AddColumn(DatabaseConstants.Attempts, "INTEGER").DefaultValue(0);
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "TEXT").NotNull();

        if (durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            AddColumn(DatabaseConstants.ReceivedAt, "TEXT");
        }
        else
        {
            AddColumn(DatabaseConstants.ReceivedAt, "TEXT").AsPrimaryKey();
        }

        AddColumn(DatabaseConstants.KeepUntil, "TEXT");

        if (durability.InboxStaleTime.HasValue)
        {
            AddColumn(DatabaseConstants.Timestamp, "TEXT").DefaultValueByExpression("(datetime('now'))");
        }
    }
}
