using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

internal class OutgoingEnvelopeTable : Table
{
    public OutgoingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new SqliteObjectName(DatabaseConstants.OutgoingTable))
    {
        AddColumn(DatabaseConstants.Id, "TEXT").AsPrimaryKey();
        AddColumn(DatabaseConstants.OwnerId, "INTEGER").NotNull();
        AddColumn(DatabaseConstants.Destination, "TEXT").NotNull();
        AddColumn(DatabaseConstants.DeliverBy, "TEXT");
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
        AddColumn(DatabaseConstants.Attempts, "INTEGER").DefaultValue(0);
        AddColumn(DatabaseConstants.MessageType, "TEXT").NotNull();

        if (durability.OutboxStaleTime.HasValue)
        {
            AddColumn(DatabaseConstants.Timestamp, "TEXT").DefaultValueByExpression("(datetime('now'))");
        }
    }
}
