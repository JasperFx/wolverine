using System;
using Weasel.Core;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

internal class OutgoingEnvelopeTable : Table
{
    public OutgoingEnvelopeTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.OutgoingTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn<string>(DatabaseConstants.Destination).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.DeliverBy);
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();

        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);

        AddColumn<Guid>(DatabaseConstants.ConversationId);
        AddColumn<string>(DatabaseConstants.CorrelationId);
        AddColumn<string>(DatabaseConstants.ParentId);
        AddColumn<string>(DatabaseConstants.SagaId);
        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<string>(DatabaseConstants.ContentType);
        AddColumn<string>(DatabaseConstants.ReplyRequested);
        AddColumn<bool>(DatabaseConstants.AckRequested);
        AddColumn<string>(DatabaseConstants.ReplyUri);

        AddColumn<DateTimeOffset>(DatabaseConstants.SentAt);
    }
}