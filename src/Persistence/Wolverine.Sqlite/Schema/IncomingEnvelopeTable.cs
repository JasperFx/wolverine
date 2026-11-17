using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

internal class IncomingEnvelopeTable : Table
{
    public IncomingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new SqliteObjectName(TablePrefixing.Apply(schemaName, DatabaseConstants.IncomingTable)))
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

        // GH-5167: the envelope's GroupId, so scheduled message management can find, cancel and reschedule the
        // scheduled messages of one business identifier (booking/{id}). Nullable, so an existing table migrates
        // by gaining a column that is null for every row already there.
        AddColumn(DatabaseConstants.GroupId, "TEXT");

        // GH-5193: always created (unless AlwaysProvisionOptionalSchemaObjects is off), whether or not
        // InboxStaleTime is set; only the statements that read and write it follow InboxStaleTime
        if (durability.ProvisionsIncomingTimestamp)
        {
            AddColumn(DatabaseConstants.Timestamp, "TEXT").DefaultValueByExpression("(datetime('now'))");
        }

        // GH-5167: serves ScheduledMessageQuery.GroupId (query, cancel, reschedule by group), partial on the Scheduled slice
        Indexes.Add(new IndexDefinition($"idx_{Identifier.Name}_group_id")
        {
            Columns = [DatabaseConstants.GroupId],
            Predicate = $"{DatabaseConstants.Status} = 'Scheduled'"
        });
    }
}
