using Weasel.Core;
using Weasel.Postgresql.Tables;

namespace Wolverine.Postgresql.Transport.NServiceBus;

/// <summary>
/// Weasel model of an NServiceBus PostgreSQL transport queue table. Mirrors the layout the
/// NServiceBus PostgreSQL transport creates, so that Wolverine's schema management — and the
/// Weasel command line tooling — can create, diff, and drop these tables the same way it does
/// its own messaging transport tables.
/// </summary>
internal class NServiceBusQueueTable : Table
{
    public NServiceBusQueueTable(DbObjectName identifier) : base(identifier)
    {
        AddColumn<Guid>("Id").AsPrimaryKey();
        AddColumn("Expires", "timestamp");
        AddColumn<string>("Headers").NotNull();
        AddColumn("Body", "bytea");

        // Seq is the auto-incrementing column NServiceBus orders on for FIFO receive. We model it
        // only as a serial column: NServiceBus adds a UNIQUE *constraint* on seq, but Weasel can
        // only express a unique *index*, and the two don't reconcile (Weasel would try to drop the
        // constraint-backed index, which PostgreSQL refuses). Since the transport never needs to
        // enforce seq uniqueness itself, leaving it off keeps schema diffs clean against the tables
        // NServiceBus owns.
        AddColumn<int>("Seq").AutoIncrement().NotNull();
    }
}
