using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Transport;

internal class ScheduledMessageTable : Table
{
    public ScheduledMessageTable(MySqlTransport settings, string tableName) : base(
        new DbObjectName(settings.TransportSchemaName, tableName))
    {
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
    }
}
