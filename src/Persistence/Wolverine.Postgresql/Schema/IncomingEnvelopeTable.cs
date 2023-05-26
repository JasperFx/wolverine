using Weasel.Core;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

internal class IncomingEnvelopeTable : Table
{
    public IncomingEnvelopeTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.IncomingTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn<string>(DatabaseConstants.Status).NotNull();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();

        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<string>(DatabaseConstants.ReceivedAt);
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
    }
}