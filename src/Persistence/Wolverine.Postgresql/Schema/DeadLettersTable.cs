using System;
using Weasel.Core;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

internal class DeadLettersTable : Table
{
    public DeadLettersTable(string schemaName) : base(new DbObjectName(schemaName, DatabaseConstants.DeadLetterTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();

        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();

        AddColumn<Guid>(DatabaseConstants.ConversationId);
        AddColumn<string>(DatabaseConstants.CorrelationId);
        AddColumn<string>(DatabaseConstants.ParentId);
        AddColumn<string>(DatabaseConstants.SagaId);
        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<string>(DatabaseConstants.ContentType);
        AddColumn<string>(DatabaseConstants.ReplyRequested);
        AddColumn<bool>(DatabaseConstants.AckRequested);
        AddColumn<string>(DatabaseConstants.ReplyUri);
        AddColumn<string>(DatabaseConstants.ReceivedAt);

        AddColumn<string>(DatabaseConstants.Source);
        AddColumn<string>(DatabaseConstants.Explanation);
        AddColumn<string>(DatabaseConstants.ExceptionText);
        AddColumn<string>(DatabaseConstants.ExceptionType);
        AddColumn<string>(DatabaseConstants.ExceptionMessage);

        AddColumn<DateTimeOffset>(DatabaseConstants.SentAt);
    }
}