using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Transport;

internal class QueueTable : Table
{
    public QueueTable(MySqlTransport parent, string tableName) : base(
        new DbObjectName(parent.TransportSchemaName, tableName))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "LONGBLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "VARCHAR(500)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("(UTC_TIMESTAMP(6))");
    }
}
