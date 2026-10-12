using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Oracle.Transport;

internal class ScheduledMessageTable : Table
{
    public ScheduledMessageTable(OracleTransport parent, string tableName) : base(
        new OracleObjectName(parent.TransportSchemaName.ToUpperInvariant(), tableName.ToUpperInvariant()))
    {
        // Never drop the opt-in ordering column or its index if the option is later turned off
        AddOnlyMigrations = true;

        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "VARCHAR2(500)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("SYSTIMESTAMP AT TIME ZONE 'UTC'");

        if (parent.EnableDeterministicQueueOrdering)
        {
            // Keeps scheduled rows that share an execution time in sequence when they are promoted
            AddColumn(QueueTable.BuildQueueOrderColumn());
        }

        Indexes.Add(new IndexDefinition($"idx_{tableName}_execution_time")
        {
            Columns = [DatabaseConstants.ExecutionTime]
        });

        if (parent.EnableDeterministicQueueOrdering)
        {
            // A separate name makes enabling this additive and leaves the legacy index untouched
            Indexes.Add(new IndexDefinition($"idx_{tableName}_exec_order")
            {
                Columns = [DatabaseConstants.ExecutionTime, QueueTable.QueueOrderColumn]
            });
        }
    }
}
