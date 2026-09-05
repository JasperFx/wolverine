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

        // GH-4316: the owner index above deliberately excludes owner_id = 0, but that is exactly
        // what the 5-second recovery poll asks for (`status = 'Incoming' and owner_id = 0 group by
        // received_at`) and what the per-listener recovery page load filters on — so recovery was
        // a full scan of an inbox dominated by retained Handled rows, every cycle, per database,
        // per node. Filtered on the recoverable predicate so the index holds only unowned incoming
        // rows and the poll degrades to an empty index seek when there is nothing to recover.
        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.IncomingTable}_recover")
        {
            Columns = [DatabaseConstants.ReceivedAt],
            Predicate = $"[{DatabaseConstants.Status}]='Incoming' AND [{DatabaseConstants.OwnerId}]=0"
        });

        // GH-4316: the 60-second expired-handled cleanup probes `status = 'Handled' and
        // keep_until <= now`, which had no supporting index — a full scan of the largest slice of
        // the inbox on every cycle. Filtered on the Handled slice so the probe only ever touches
        // rows the sweep could delete.
        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.IncomingTable}_keep_until")
        {
            Columns = [DatabaseConstants.KeepUntil],
            Predicate = $"[{DatabaseConstants.Status}]='Handled'"
        });
    }
}