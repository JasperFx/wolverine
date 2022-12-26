using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

internal class OutgoingEnvelopeTable : Table
{
    public OutgoingEnvelopeTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.OutgoingTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn(DatabaseConstants.Destination, "varchar(250)").NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.DeliverBy);
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();

        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn<Guid>(DatabaseConstants.ConversationId);
        AddColumn<string>(DatabaseConstants.CorrelationId);
        AddColumn<string>(DatabaseConstants.ParentId);
        AddColumn<string>(DatabaseConstants.SagaId);
        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();
        AddColumn<string>(DatabaseConstants.ContentType);
        AddColumn(DatabaseConstants.ReplyRequested, "varchar(250)");
        AddColumn<bool>(DatabaseConstants.AckRequested);
        AddColumn<string>(DatabaseConstants.ReplyUri);
        AddColumn<DateTimeOffset>(DatabaseConstants.SentAt);
    }
}