using System;
using Wolverine.RDBMS;
using Weasel.Core;
using Weasel.SqlServer.Tables;

namespace Wolverine.SqlServer.Schema;

internal class DeadLettersTable : Table
{
    public DeadLettersTable(string schemaName) : base(new DbObjectName(schemaName, DatabaseConstants.DeadLetterTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();

        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();

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

        AddColumn(DatabaseConstants.Source, "varchar(250)");
        AddColumn(DatabaseConstants.Explanation, "varchar(max)");
        AddColumn(DatabaseConstants.ExceptionText, "varchar(max)");
        AddColumn(DatabaseConstants.ExceptionType, "varchar(max)");
        AddColumn(DatabaseConstants.ExceptionMessage, "varchar(max)");
    }
}
