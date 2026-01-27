using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

internal class OutgoingEnvelopeTable : Table
{
    public OutgoingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.OutgoingTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn(DatabaseConstants.Destination, "varchar(250)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.DeliverBy);
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();

        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();

        if (durability.OutboxStaleTime.HasValue)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Timestamp).DefaultValueByExpression("GETUTCDATE()");
        }
    }
}