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
        // Lowercase deliberately. A real NServiceBus-provisioned queue table has case-folded
        // column names — verified against a live NServiceBus.Transport.PostgreSql host — so
        // these have to be lowercase to line up with a table NServiceBus owns, and to keep the
        // unquoted identifiers in the send/receive SQL resolving. Weasel used to case-fold
        // declared names on its way to the database and hid the difference; as of 9.25 it
        // preserves and quotes what it is given, which would have made "Seq" a genuinely
        // distinct column from NServiceBus's seq.
        AddColumn<Guid>("id").AsPrimaryKey();
        AddColumn("expires", "timestamp");
        AddColumn<string>("headers").NotNull();
        AddColumn("body", "bytea");

        AddColumn<int>("seq").AutoIncrement().NotNull();

        // seq is what the destructive receive orders by (FIFO), so a Wolverine-provisioned table
        // needs an index on it or the ORDER BY degrades to a full sort once a backlog builds — a
        // soak that let the table grow collapsed receiver throughput by ~60x without this. We use a
        // *non-unique* index (NOT a unique one): NServiceBus puts a UNIQUE *constraint* on seq and
        // Weasel can only express a unique *index*, so a unique index would make Weasel try to drop
        // the constraint-backed index (which PostgreSQL refuses) when reconciling against an
        // NServiceBus-owned table. The transport never needs to enforce seq uniqueness itself.
        Indexes.Add(new IndexDefinition(PostgresqlIdentifier.Shorten($"idx_{identifier.Name}_seq"))
        {
            Columns = ["seq"]
        });
    }
}
