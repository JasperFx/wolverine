using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Oracle.Schema;

internal class IncomingEnvelopeTable : Table
{
    public IncomingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new OracleObjectName(schemaName.ToUpperInvariant(), DatabaseConstants.IncomingTable.ToUpperInvariant()))
    {
        AddColumn<Guid>(DatabaseConstants.Id).NotNull().AsPrimaryKey();
        AddColumn("status", "VARCHAR2(25)").NotNull();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime);
        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();

        AddColumn(DatabaseConstants.MessageType, "VARCHAR2(500)").NotNull();

        if (durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            AddColumn(DatabaseConstants.ReceivedAt, "VARCHAR2(500)").NotNull();
        }
        else
        {
            AddColumn(DatabaseConstants.ReceivedAt, "VARCHAR2(250)").AsPrimaryKey().NotNull();
        }

        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);

        if (durability.InboxStaleTime.HasValue)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Timestamp)
                .DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)");
        }
    }
}
