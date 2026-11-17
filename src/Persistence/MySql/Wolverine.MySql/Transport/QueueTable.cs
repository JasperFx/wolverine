using System.Data.Common;
using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Transport;

internal class QueueTable : Table
{
    private readonly bool _deterministicQueueOrdering;
    private readonly string _uniqueKeyName;
    private readonly bool _adoptExisting;

    public QueueTable(MySqlTransport parent, string tableName)
        : this(parent.TransportSchemaName, tableName, parent.EnableDeterministicQueueOrdering,
            !parent.DeterministicQueueOrderingIsExplicit)
    {
    }

    // GH-3859: the schema is resolved per data source on a multi-tenanted host, because a MySQL schema
    // IS a database and one fixed name would leave every tenant sharing a single physical queue table.
    public QueueTable(string schemaName, string tableName, bool deterministicQueueOrdering)
        : this(schemaName, tableName, deterministicQueueOrdering, false)
    {
    }

    // GH-5192: adoptExisting when deterministic ordering is only the 7.0 default rather than set explicitly
    public QueueTable(string schemaName, string tableName, bool deterministicQueueOrdering, bool adoptExisting)
        : base(new DbObjectName(schemaName, tableName))
    {
        _adoptExisting = deterministicQueueOrdering && adoptExisting;
        // Never drop what the model does not declare, so turning deterministic ordering off again
        // keeps the column and indexes it added.
        AddOnlyMigrations = true;
        _deterministicQueueOrdering = deterministicQueueOrdering;
        _uniqueKeyName = QueueOrdering.UniqueKeyName(tableName);

        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "LONGBLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "VARCHAR(500)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("(UTC_TIMESTAMP(6))");

        // GH-4862: opt-in only, so applications that have not asked for it see no schema change.
        if (deterministicQueueOrdering)
        {
            QueueOrdering.AddTo(this, tableName, "timestamp");
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
            return new TableDelta(new QueueTable(Identifier.Schema!, Identifier.Name, false), actual);
        }

        return new QueueOrderingTableDelta(this, tableDelta.Actual, _uniqueKeyName);
    }
}
