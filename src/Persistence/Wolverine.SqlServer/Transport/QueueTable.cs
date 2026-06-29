using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Transport;

internal class QueueTable : Table
{
    public QueueTable(SqlServerTransport transport, string tableName) : base(
        new DbObjectName(transport.TransportSchemaName, tableName))
    {
        var id = AddColumn<Guid>(DatabaseConstants.Id);
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();
        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("SYSDATETIMEOFFSET()");

        if (transport.OptimizeQueueThroughput)
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
        }
    }
}