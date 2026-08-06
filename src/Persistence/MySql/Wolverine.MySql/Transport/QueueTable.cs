using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Transport;

internal class QueueTable : Table
{
    public QueueTable(MySqlTransport parent, string tableName) : this(parent.TransportSchemaName, tableName)
    {
    }

    // GH-3859: the schema is resolved per data source on a multi-tenanted host, because a MySQL schema
    // IS a database and one fixed name would leave every tenant sharing a single physical queue table.
    public QueueTable(string schemaName, string tableName) : base(new DbObjectName(schemaName, tableName))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "LONGBLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "VARCHAR(500)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("(UTC_TIMESTAMP(6))");
    }
}
