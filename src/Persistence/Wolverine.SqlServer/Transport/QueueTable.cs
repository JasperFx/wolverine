using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Transport;

internal class QueueTable : Table
{
    public QueueTable(SqlServerTransport transport, string tableName) : base(
        new DbObjectName(transport.TransportSchemaName, tableName))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();
        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("SYSDATETIMEOFFSET()");
    }
}