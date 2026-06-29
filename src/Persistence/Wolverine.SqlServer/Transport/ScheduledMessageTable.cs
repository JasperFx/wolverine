using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Transport;

internal class ScheduledMessageTable : Table
{
    public ScheduledMessageTable(SqlServerTransport transport, string tableName) : base(
        new DbObjectName(transport.TransportSchemaName, tableName))
    {
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

        if (transport.OptimizeQueueThroughput)
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
        }
    }
}