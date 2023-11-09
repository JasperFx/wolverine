using Weasel.Core;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Transport;

internal class ScheduledMessageTable : Table
{
    public ScheduledMessageTable(DatabaseSettings settings, string tableName) : base(
        new DbObjectName(settings.SchemaName, tableName))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();
        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("timezone('UTC', now())");
        
        // Definitely want to index the execution time. Far more reads than writes. We think. 
        Indexes.Add(new IndexDefinition($"idx_{tableName}_execution_time")
        {
            Columns = new[]{DatabaseConstants.ExecutionTime}
        });
    }
}