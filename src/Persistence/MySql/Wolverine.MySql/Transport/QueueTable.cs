using System.Data.Common;
using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Transport;

internal class QueueTable : Table
{
    private readonly bool _deterministicQueueOrdering;
    private readonly string _uniqueKeyName;

    public QueueTable(MySqlTransport parent, string tableName)
        : this(parent.TransportSchemaName, tableName, parent.EnableDeterministicQueueOrdering)
    {
    }

    // GH-3859: the schema is resolved per data source on a multi-tenanted host, because a MySQL schema
    // IS a database and one fixed name would leave every tenant sharing a single physical queue table.
    public QueueTable(string schemaName, string tableName, bool deterministicQueueOrdering)
        : base(new DbObjectName(schemaName, tableName))
    {
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

        return new QueueOrderingTableDelta(this, tableDelta.Actual, _uniqueKeyName);
    }
}
