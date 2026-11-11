using System.Data.Common;
using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Transport;

internal class ScheduledMessageTable : Table
{
    private readonly SqlServerTransport _transport;
    private readonly string _tableName;
    private readonly QueueTableShape _shape;

    public ScheduledMessageTable(SqlServerTransport transport, string tableName, QueueTableShape shape) : base(
        new DbObjectName(transport.TransportSchemaName, tableName))
    {
        _transport = transport;
        _tableName = tableName;
        _shape = shape;
        var optimizeThroughput = shape.OptimizeThroughput;

        var id = AddColumn<Guid>(DatabaseConstants.Id);
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();
        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("SYSDATETIMEOFFSET()");

        // Definitely want to index the execution time. Far more reads than writes. We think.
        Indexes.Add(new IndexDefinition($"idx_{tableName}_execution_time")
        {
            Columns = [DatabaseConstants.ExecutionTime]
        });

        if (optimizeThroughput)
        {
            // Match the high-throughput layout of the ready queue table (see OptimizeQueueThroughput()):
            // cluster on a monotonic identity, keep the id unique and non-clustered for idempotent
            // sends, and use a filtered index for the expiry sweep.
            id.NotNull();
            AddColumn<long>("seq").AutoNumber().NotNull();

            Indexes.Add(new IndexDefinition($"cidx_{tableName}_seq")
            {
                Columns = ["seq"],
                IsClustered = true
            });

            Indexes.Add(new IndexDefinition($"uidx_{tableName}_id")
            {
                Columns = [DatabaseConstants.Id],
                IsUnique = true
            });

            Indexes.Add(new IndexDefinition($"idx_{tableName}_keepuntil")
            {
                Columns = [DatabaseConstants.KeepUntil],
                Predicate = $"{DatabaseConstants.KeepUntil} IS NOT NULL"
            });
        }
        else
        {
            id.AsPrimaryKey();

            // GH-4862: see QueueTable. The high-throughput layout's seq identity is already the
            // tie-breaker there (the execution_time index carries the clustering key), so only the
            // default layout needs the opt-in column.
            AddOnlyMigrations = true;

            if (shape.QueueOrder)
            {
                // Preserve a deterministic order when multiple scheduled rows are promoted together.
                AddColumn(new QueueOrderingColumn());

                Indexes.Add(new IndexDefinition($"idx_{tableName}_execution_time_queue_order")
                {
                    Columns = [DatabaseConstants.ExecutionTime, QueueTable.QueueOrderColumn]
                });
            }
        }
    }

    // GH-4989: an existing table on the other OptimizeQueueThroughput() layout is rebuilt, not migrated
    // in place. See QueueTableLayoutSwitchDelta. GH-5192: but only when that layout was set explicitly; an
    // existing table is otherwise adopted on the layout it has. See QueueTableShape
    public override async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default)
    {
        var delta = await base.CreateDeltaAsync(reader, ct);
        if (delta is not TableDelta { Actual: { } actual }) return delta;

        var shape = _shape.ToMatch(actual);
        var expected = shape == _shape.Fixed ? this : new ScheduledMessageTable(_transport, _tableName, shape);

        if (QueueTableLayoutSwitchDelta.IsLayoutSwitch(expected, actual))
        {
            return new QueueTableLayoutSwitchDelta(expected, actual, DatabaseConstants.ExecutionTime);
        }

        return ReferenceEquals(expected, this) ? delta : new TableDelta(expected, actual);
    }
}
