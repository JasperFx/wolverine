using Weasel.Core;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

internal class DeadLettersTable : Table
{
    public DeadLettersTable(DurabilitySettings durability, string schemaName) : base(new DbObjectName(schemaName, DatabaseConstants.DeadLetterTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();

        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
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

        AddColumn<string>(DatabaseConstants.Source);
        AddColumn<string>(DatabaseConstants.ExceptionType);
        AddColumn<string>(DatabaseConstants.ExceptionMessage);

        AddColumn<DateTimeOffset>(DatabaseConstants.SentAt);
        AddColumn<bool>(DatabaseConstants.Replayable);
    }
}