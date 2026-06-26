using Weasel.Core;
using Weasel.Postgresql;
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

        AddColumn<int>("Seq").AutoIncrement().NotNull();

        // Seq is what the destructive receive orders by (FIFO), so a Wolverine-provisioned table
        // needs an index on it or the ORDER BY degrades to a full sort once a backlog builds — a
        // soak that let the table grow collapsed receiver throughput by ~60x without this. We use a
        // *non-unique* index (NOT a unique one): NServiceBus puts a UNIQUE *constraint* on seq and
        // Weasel can only express a unique *index*, so a unique index would make Weasel try to drop
        // the constraint-backed index (which PostgreSQL refuses) when reconciling against an
        // NServiceBus-owned table. The transport never needs to enforce seq uniqueness itself.
        Indexes.Add(new IndexDefinition(PostgresqlIdentifier.Shorten($"idx_{identifier.Name}_seq"))
        {
            Columns = ["Seq"]
        });
    }
}
