using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

internal class DeadLettersTable : Table
{
    public DeadLettersTable(string schemaName) : base(new DbObjectName(schemaName, DatabaseConstants.DeadLetterTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();

        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();

        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();
        AddColumn(DatabaseConstants.ReceivedAt, "varchar(250)");

        AddColumn(DatabaseConstants.Source, "varchar(250)");
        AddColumn(DatabaseConstants.ExceptionType, "varchar(max)");
        AddColumn(DatabaseConstants.ExceptionMessage, "varchar(max)");

        AddColumn<DateTimeOffset>(DatabaseConstants.SentAt);
        AddColumn<bool>(DatabaseConstants.Replayable);
    }
}