using System.Data.Common;
using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Transport;

internal class ScheduledMessageTable : Table
{
    private readonly bool _deterministicQueueOrdering;
    private readonly string _uniqueKeyName;

    public ScheduledMessageTable(MySqlTransport settings, string tableName)
        : this(settings.TransportSchemaName, tableName, settings.EnableDeterministicQueueOrdering)
    {
    }

    // GH-3859: see the matching comment on QueueTable.
    public ScheduledMessageTable(string schemaName, string tableName, bool deterministicQueueOrdering)
        : base(new DbObjectName(schemaName, tableName))
    {
        // See QueueTable: disabling deterministic ordering later never drops what it added.
        AddOnlyMigrations = true;
        _deterministicQueueOrdering = deterministicQueueOrdering;
        _uniqueKeyName = QueueOrdering.UniqueKeyName(tableName);

        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "LONGBLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "VARCHAR(500)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("(UTC_TIMESTAMP(6))");

        Indexes.Add(new IndexDefinition($"idx_{tableName}_execution_time")
        {
            Columns = [DatabaseConstants.ExecutionTime]
        });

        // GH-4862: keeps scheduled messages that come due together in sequence when promoted.
        if (deterministicQueueOrdering)
        {
            QueueOrdering.AddTo(this, tableName, DatabaseConstants.ExecutionTime);
        }
    }

    public override async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default)
    {
        var delta = await base.CreateDeltaAsync(reader, ct);
        if (!_deterministicQueueOrdering || delta is not TableDelta tableDelta) return delta;

        return new QueueOrderingTableDelta(this, tableDelta.Actual, _uniqueKeyName);
    }
}
