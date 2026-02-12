using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Oracle.Schema;

internal class OutgoingEnvelopeTable : Table
{
    public OutgoingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new OracleObjectName(schemaName.ToUpperInvariant(), DatabaseConstants.OutgoingTable.ToUpperInvariant()))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn(DatabaseConstants.Destination, "VARCHAR2(500)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.DeliverBy);
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();

        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn(DatabaseConstants.MessageType, "VARCHAR2(500)").NotNull();

        if (durability.OutboxStaleTime.HasValue)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Timestamp)
                .DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)");
        }
    }
}
