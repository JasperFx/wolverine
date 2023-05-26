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
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();

        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<string>(DatabaseConstants.ReceivedAt);

        AddColumn<string>(DatabaseConstants.Source);
        AddColumn<string>(DatabaseConstants.ExceptionType);
        AddColumn<string>(DatabaseConstants.ExceptionMessage);

        AddColumn<DateTimeOffset>(DatabaseConstants.SentAt);
        AddColumn<bool>(DatabaseConstants.Replayable);
    }
}