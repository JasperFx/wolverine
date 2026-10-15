using System.Data.Common;
using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Transport;

internal class QueueTable : Table
{
    internal const string QueueOrderColumn = "queue_order";

    public QueueTable(SqlServerTransport transport, string tableName, bool optimizeThroughput) : base(
        new DbObjectName(transport.TransportSchemaName, tableName))
    {
        var id = AddColumn<Guid>(DatabaseConstants.Id);
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();
        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("SYSDATETIMEOFFSET()");

        if (optimizeThroughput)
        {
            // Opt-in high-throughput layout (see OptimizeQueueThroughput()): cluster on a monotonic
            // identity so the TOP(n) ... ORDER BY seq dequeue is a clustered seek and the matching
            // DELETE removes physically contiguous rows; keep the message id unique (non-clustered)
            // so duplicate sends still fail fast for idempotency; and use a filtered index for the
            // expiry sweep. Mirrors the proven NServiceBus SQL Server transport layout.
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
            // Default layout: clustered PK on id, plus an index on timestamp so the TOP(n) ...
            // ORDER BY timestamp dequeue is a seek rather than a full scan + sort (the clustered PK
            // on a random Guid is useless for that ordering). See GH perf review.
            id.AsPrimaryKey();

            Indexes.Add(new IndexDefinition($"idx_{tableName}_timestamp")
            {
                Columns = ["timestamp"]
            });

            // GH-4862: never drop what the model does not declare, so turning
            // EnableDeterministicQueueOrdering off again keeps the queue_order column and its index.
            // Only the default layout gets this: the high-throughput layout never adds queue_order,
            // so there is nothing there to protect and its migrations are left exactly as they were.
            AddOnlyMigrations = true;

            // GH-4862: SYSDATETIMEOFFSET() is evaluated once per statement, so every row a promotion
            // inserts shares one timestamp. The persistent identity is an opt-in schema change that
            // breaks those ties in insertion order. The separate index name keeps this additive and
            // the legacy index definition stable for applications that have not opted in.
            if (transport.EnableDeterministicQueueOrdering)
            {
                AddColumn(new QueueOrderingColumn());

                Indexes.Add(new IndexDefinition($"idx_{tableName}_timestamp_queue_order")
                {
                    Columns = ["timestamp", QueueOrderColumn]
                });
            }
        }
    }

    // GH-4989: an existing table on the other OptimizeQueueThroughput() layout is rebuilt, not migrated
    // in place. See QueueTableLayoutSwitchDelta
    public override async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default)
    {
        var delta = await base.CreateDeltaAsync(reader, ct);
        return delta is TableDelta tableDelta && QueueTableLayoutSwitchDelta.IsLayoutSwitch(this, tableDelta.Actual)
            ? new QueueTableLayoutSwitchDelta(this, tableDelta.Actual!, "timestamp")
            : delta;
    }
}
