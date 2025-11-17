using Weasel.Core;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

internal class IncomingEnvelopeTable : Table
{
    public IncomingEnvelopeTable(DurabilitySettings durability, string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.IncomingTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn<string>(DatabaseConstants.Status).NotNull();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();

        AddColumn<string>(DatabaseConstants.MessageType).NotNull();

        if (durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            AddColumn<string>(DatabaseConstants.ReceivedAt);
        }
        else
        {
            AddColumn<string>(DatabaseConstants.ReceivedAt).AsPrimaryKey();
        }
        
        
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);

        if (durability.EnableInboxPartitioning)
        {
            ModifyColumn(DatabaseConstants.Status).AsPrimaryKey();
            PartitionByList(DatabaseConstants.Status)
                .AddPartition("incoming", EnvelopeStatus.Incoming.ToString())
                .AddPartition("scheduled", EnvelopeStatus.Scheduled.ToString())
                .AddPartition("handled", EnvelopeStatus.Handled.ToString());
        }
    }
}