using System.Data.Common;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Transport;

internal class ScheduledMessageTable : Table
{
    private readonly PostgresqlTransport _settings;
    private readonly string _tableName;
    private readonly bool _adoptExisting;

    public ScheduledMessageTable(PostgresqlTransport settings, string tableName) : this(settings, tableName,
        settings.EnableDeterministicQueueOrdering, !settings.DeterministicQueueOrderingIsExplicit)
    {
    }

    private ScheduledMessageTable(PostgresqlTransport settings, string tableName, bool deterministicOrdering,
        bool adoptExisting) : base(new DbObjectName(settings.TransportSchemaName, tableName))
    {
        _settings = settings;
        _tableName = tableName;
        _adoptExisting = deterministicOrdering && adoptExisting;

        AddOnlyMigrations = true;

        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();
        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("now()");

        if (deterministicOrdering)
        {
            // Preserve a deterministic order when multiple scheduled rows are promoted together.
            AddColumn(new QueueOrderingColumn());
        }

        // Definitely want to index the execution time. Far more reads than writes. We think.
        //
        // GH-2942: the constructed index name `idx_{tableName}_execution_time` can exceed
        // PostgreSQL's NAMEDATALEN (default 63 chars) even when `tableName` itself is within
        // limits. Use PostgresqlIdentifier.Shorten() to predictably stay under the limit with a
        // deterministic hash suffix so the in-memory model matches what PostgreSQL stores; that
        // keeps the schema-diff clean rather than failing with "Missing known broker resources".
        var indexName = PostgresqlIdentifier.Shorten($"idx_{tableName}_execution_time");
        Indexes.Add(new IndexDefinition(indexName) { Columns = [DatabaseConstants.ExecutionTime] });

        if (deterministicOrdering)
        {
            var orderedIndexName = PostgresqlIdentifier.Shorten($"idx_{tableName}_execution_time_queue_order");
            Indexes.Add(new IndexDefinition(orderedIndexName)
            {
                Columns = [DatabaseConstants.ExecutionTime, QueueTable.QueueOrderColumn]
            });
        }
    }

    // GH-5192: see QueueTable. An existing table without queue_order is adopted on the classic layout.
    public override async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default)
    {
        var delta = await base.CreateDeltaAsync(reader, ct);
        if (_adoptExisting && delta is TableDelta { Actual: { } actual } && !actual.HasColumn(QueueTable.QueueOrderColumn))
        {
            return new TableDelta(new ScheduledMessageTable(_settings, _tableName, false, false), actual);
        }

        return delta;
    }
}
