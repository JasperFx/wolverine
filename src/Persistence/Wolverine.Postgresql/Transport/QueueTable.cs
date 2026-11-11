using System.Data.Common;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Transport;

internal class QueueTable : Table
{
    internal const string QueueOrderColumn = "queue_order";

    private readonly PostgresqlTransport _parent;
    private readonly string _tableName;
    private readonly bool _adoptExisting;

    public QueueTable(PostgresqlTransport parent, string tableName) : this(parent, tableName,
        parent.EnableDeterministicQueueOrdering, !parent.DeterministicQueueOrderingIsExplicit)
    {
    }

    private QueueTable(PostgresqlTransport parent, string tableName, bool deterministicOrdering, bool adoptExisting) :
        base(new DbObjectName(parent.TransportSchemaName, tableName))
    {
        _parent = parent;
        _tableName = tableName;
        _adoptExisting = deterministicOrdering && adoptExisting;

        AddOnlyMigrations = true;

        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();
        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("now()");

        // Keep the released schema as the default. The persistent sequence is an opt-in schema
        // change because PostgreSQL's now() is fixed for a transaction and promoted rows can tie.
        if (deterministicOrdering)
        {
            AddColumn(new QueueOrderingColumn());
        }

        var indexName = PostgresqlIdentifier.Shorten($"idx_{tableName}_timestamp");
        Indexes.Add(new IndexDefinition(indexName) { Columns = ["timestamp"] });

        if (deterministicOrdering)
        {
            // The separate name makes enabling this additive and keeps the legacy index definition
            // stable for applications that have not opted in.
            var orderedIndexName = PostgresqlIdentifier.Shorten($"idx_{tableName}_timestamp_queue_order");
            Indexes.Add(new IndexDefinition(orderedIndexName)
            {
                Columns = ["timestamp", QueueOrderColumn]
            });
        }
    }

    // GH-5192: deterministic ordering is the 7.0 default for a table Wolverine CREATES. An existing table
    // without queue_order keeps its layout: adding an identity to it rewrites the whole table under an
    // exclusive lock. So diff that table against the classic layout, and the migration leaves it alone. An
    // explicit EnableDeterministicQueueOrdering() still migrates it, as it did in 6.x.
    public override async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default)
    {
        var delta = await base.CreateDeltaAsync(reader, ct);
        if (_adoptExisting && delta is TableDelta { Actual: { } actual } && !actual.HasColumn(QueueOrderColumn))
        {
            return new TableDelta(new QueueTable(_parent, _tableName, false, false), actual);
        }

        return delta;
    }
}
