using Weasel.Core;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

internal class OutgoingEnvelopeTable : Table
{
    public OutgoingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.OutgoingTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn<string>(DatabaseConstants.Destination).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.DeliverBy);
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();

        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);

        AddColumn<string>(DatabaseConstants.MessageType).NotNull();

        if (durability.OutboxStaleTime.HasValue)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Timestamp).DefaultValueByExpression("(now() at time zone 'utc')");
        }
    }
}