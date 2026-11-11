using System.Data.Common;
using Weasel.Core;
using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Oracle.Transport;

internal class ScheduledMessageTable : Table
{
    private readonly OracleTransport _parent;
    private readonly string _tableName;
    private readonly bool _adoptExisting;

    public ScheduledMessageTable(OracleTransport parent, string tableName) : this(parent, tableName,
        parent.EnableDeterministicQueueOrdering, !parent.DeterministicQueueOrderingIsExplicit)
    {
    }

    private ScheduledMessageTable(OracleTransport parent, string tableName, bool deterministicOrdering, bool adoptExisting) : base(
        new OracleObjectName(parent.TransportSchemaName.ToUpperInvariant(), tableName.ToUpperInvariant()))
    {
        _parent = parent;
        _tableName = tableName;
        _adoptExisting = deterministicOrdering && adoptExisting;

        // Never drop the opt-in ordering column or its index if the option is later turned off
        AddOnlyMigrations = true;

        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "VARCHAR2(500)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("SYSTIMESTAMP AT TIME ZONE 'UTC'");

        if (deterministicOrdering)
        {
            // Keeps scheduled rows that share an execution time in sequence when they are promoted
            AddColumn(QueueTable.BuildQueueOrderColumn());
        }

        Indexes.Add(new IndexDefinition($"idx_{tableName}_execution_time")
        {
            Columns = [DatabaseConstants.ExecutionTime]
        });

        if (deterministicOrdering)
        {
            // A separate name makes enabling this additive and leaves the legacy index untouched
            Indexes.Add(new IndexDefinition($"idx_{tableName}_exec_order")
            {
                Columns = [DatabaseConstants.ExecutionTime, QueueTable.QueueOrderColumn]
            });
        }
    }

    // GH-5192: deterministic ordering is the 7.0 default for a table Wolverine CREATES. An existing table without
    // queue_order keeps its layout, because adding an identity numbers every row. Diff it against the classic
    // layout so no migration touches it. An explicit EnableDeterministicQueueOrdering() still migrates it, as in 6.x.
    public override async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default)
    {
        var delta = await base.CreateDeltaAsync(reader, ct);
        if (_adoptExisting && delta is TableDelta { Actual: { } actual } && !actual.HasColumn(QueueTable.QueueOrderColumn))
        {
            return new TableDelta(new ScheduledMessageTable(_parent, _tableName, false, false), actual);
        }

        return delta;
    }
}
