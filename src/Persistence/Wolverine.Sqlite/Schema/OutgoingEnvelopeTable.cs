using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

internal class OutgoingEnvelopeTable : Table
{
    public OutgoingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new SqliteObjectName(TablePrefixing.Apply(schemaName, DatabaseConstants.OutgoingTable)))
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

            // GH-5016: opt-in, for the stale owner-0 predicate of a ServerlessWithOutbox durability pass
            // (`owner_id = 0 and timestamp <= @stale`). Partial, so it only covers the rows a pass looks at
            if (durability.EnableOutgoingTimestampIndex)
            {
                Indexes.Add(new IndexDefinition($"idx_{Identifier.Name}_stale")
                {
                    Columns = [DatabaseConstants.Timestamp],
                    Predicate = $"{DatabaseConstants.OwnerId} = 0"
                });
            }
        }
    }
}
