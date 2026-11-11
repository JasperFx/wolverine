using System.Data.Common;
using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Transport;

internal class ScheduledMessageTable : Table
{
    private readonly bool _deterministicQueueOrdering;
    private readonly string _uniqueKeyName;
    private readonly bool _adoptExisting;

    public ScheduledMessageTable(MySqlTransport settings, string tableName)
        : this(settings.TransportSchemaName, tableName, settings.EnableDeterministicQueueOrdering,
            !settings.DeterministicQueueOrderingIsExplicit)
    {
    }

    // GH-3859: see the matching comment on QueueTable.
    public ScheduledMessageTable(string schemaName, string tableName, bool deterministicQueueOrdering)
        : this(schemaName, tableName, deterministicQueueOrdering, false)
    {
    }

    // GH-5192: adoptExisting when deterministic ordering is only the 7.0 default rather than set explicitly
    public ScheduledMessageTable(string schemaName, string tableName, bool deterministicQueueOrdering, bool adoptExisting)
        : base(new DbObjectName(schemaName, tableName))
    {
        _adoptExisting = deterministicQueueOrdering && adoptExisting;
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

        // MySQL refuses identifiers over 64 characters, which this name passed for queue names over 19
        // characters and failed host startup. Shorten() leaves every name that fit unchanged, so no
        // existing index is renamed.
        Indexes.Add(new IndexDefinition(MySqlIdentifier.Shorten($"idx_{tableName}_execution_time"))
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

        // GH-5192: ordering is the 7.0 default for a table Wolverine CREATES. An existing table without
        // queue_order keeps its layout, because adding the AUTO_INCREMENT column rewrites the table and numbers
        // every row. Diff it against the classic layout so no migration touches it. An explicit
        // EnableDeterministicQueueOrdering() still migrates it, as in 6.x.
        if (_adoptExisting && tableDelta.Actual is { } actual && !actual.HasColumn(QueueOrdering.QueueOrderColumn))
        {
            return new TableDelta(new ScheduledMessageTable(Identifier.Schema!, Identifier.Name, false), actual);
        }

        return new QueueOrderingTableDelta(this, tableDelta.Actual, _uniqueKeyName);
    }
}
