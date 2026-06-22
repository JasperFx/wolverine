using Weasel.Core;
using Weasel.SqlServer.Tables;

namespace Wolverine.SqlServer.Transport.NServiceBus;

/// <summary>
/// Weasel model of an NServiceBus SQL Server transport queue table. Mirrors the layout the
/// NServiceBus SQL Server transport creates (including the legacy
/// CorrelationId/ReplyToAddress/Recoverable columns the transport still probes), so that
/// Wolverine's schema management — and the Weasel command line tooling — can create, diff,
/// and drop these tables the same way it does its own messaging transport tables.
/// </summary>
internal class NServiceBusQueueTable : Table
{
    public NServiceBusQueueTable(DbObjectName identifier) : base(identifier)
    {
        AddColumn<Guid>("Id").NotNull();
        AddColumn("CorrelationId", "varchar(255)");
        AddColumn("ReplyToAddress", "varchar(255)");
        AddColumn<bool>("Recoverable").NotNull();
        AddColumn("Expires", "datetime");
        AddColumn("Headers", "nvarchar(max)").NotNull();
        AddColumn("Body", "varbinary(max)");
        AddColumn<long>("RowVersion").AutoNumber().NotNull();

        // NServiceBus clusters on RowVersion for FIFO receive ordering and keeps a filtered
        // index on Expires for the expiry sweep.
        Indexes.Add(new IndexDefinition("Index_RowVersion")
        {
            Columns = ["RowVersion"],
            IsClustered = true
        });

        Indexes.Add(new IndexDefinition("Index_Expires")
        {
            Columns = ["Expires"],
            Predicate = "Expires IS NOT NULL"
        });
    }
}
