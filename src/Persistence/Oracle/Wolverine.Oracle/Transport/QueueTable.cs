using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Oracle.Transport;

internal class QueueTable : Table
{
    public QueueTable(OracleTransport parent, string tableName) : base(
        new OracleObjectName(parent.TransportSchemaName.ToUpperInvariant(), tableName.ToUpperInvariant()))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
        AddColumn(DatabaseConstants.MessageType, "VARCHAR2(500)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)");
    }
}
