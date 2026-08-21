using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

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
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();

        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();

        if (durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            AddColumn(DatabaseConstants.ReceivedAt, "varchar(250)").NotNull();
        }
        else
        {
            AddColumn(DatabaseConstants.ReceivedAt, "varchar(250)").AsPrimaryKey().NotNull();
        }
        
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        
        if (durability.InboxStaleTime.HasValue)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Timestamp).DefaultValueByExpression("GETUTCDATE()");
        }

        // GH-3971: the orphaned-message sweep asks `owner_id in (<dead owners>)`, worked out in memory
        // first, precisely so it can use this index. The predicate it replaced --
        // `owner_id <> 0 and owner_id not in (<live nodes>)` -- could not: the selective part is the
        // NOT IN, and everything else matches nearly every row in a healthy fleet, so it was a full scan
        // per database on every polling cycle. Partial because owner_id = 0 (unowned) is the one value
        // the sweep never asks for and, on a busy inbox, a large share of the rows.
        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.IncomingTable}_owner")
        {
            Columns = [DatabaseConstants.OwnerId],
            Predicate = $"[{DatabaseConstants.OwnerId}]<>0"
        });

    }
}