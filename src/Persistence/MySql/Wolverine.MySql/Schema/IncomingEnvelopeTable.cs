using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Schema;

internal class IncomingEnvelopeTable : Table
{
    public IncomingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.IncomingTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).NotNull().AsPrimaryKey();
        AddColumn("status", "varchar(25)").NotNull();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn(DatabaseConstants.Body, "longblob").NotNull();

        AddColumn(DatabaseConstants.MessageType, "varchar(500)").NotNull();

        if (durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            AddColumn(DatabaseConstants.ReceivedAt, "varchar(500)").NotNull();
        }
        else
        {
            AddColumn(DatabaseConstants.ReceivedAt, "varchar(250)").AsPrimaryKey().NotNull();
        }

        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);

        // GH-5193: always created (unless AlwaysProvisionOptionalSchemaObjects is off), whether or not
        // InboxStaleTime is set; only the statements that read and write it follow InboxStaleTime
        if (durability.ProvisionsIncomingTimestamp)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Timestamp).DefaultValueByExpression("(UTC_TIMESTAMP(6))");
        }

        // GH-3971: the orphaned-message sweep asks `owner_id in (<dead owners>)`, worked out in memory
        // first, precisely so it can use this index. The predicate it replaced --
        // `owner_id <> 0 and owner_id not in (<live nodes>)` -- could not use one. Not partial here:
        // this provider's IndexDefinition has no predicate support.
        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.IncomingTable}_owner")
        {
            Columns = [DatabaseConstants.OwnerId]
        });

        // GH-5428: the expired-handled cleanup (status = 'Handled' and keep_until <= now) had nothing to
        // use here, so under REPEATABLE READ it walked the clustered index and X-locked every row it looked at,
        // the Incoming rows live handlers were about to mark Handled among them, and InnoDB picked those
        // handler commits as deadlock victims. With the status leading, the delete only touches the expired
        // Handled range. SQL Server's equivalent is the filtered idx_..._keep_until from GH-4316.
        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.IncomingTable}_keep_until")
        {
            Columns = [DatabaseConstants.Status, DatabaseConstants.KeepUntil]
        });

    }
}
