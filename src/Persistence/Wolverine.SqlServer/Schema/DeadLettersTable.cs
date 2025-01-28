using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

internal class DeadLettersTable : Table
{
    public DeadLettersTable(DurabilitySettings durability, string schemaName) : base(new DbObjectName(schemaName, DatabaseConstants.DeadLetterTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).NotNull().AsPrimaryKey();

        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();

        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();
        
        if (durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            AddColumn(DatabaseConstants.ReceivedAt, "varchar(250)").NotNull();
        }
        else
        {
            AddColumn(DatabaseConstants.ReceivedAt, "varchar(250)").AsPrimaryKey().NotNull();
        }

        AddColumn(DatabaseConstants.Source, "varchar(250)");
        AddColumn(DatabaseConstants.ExceptionType, "varchar(max)");
        AddColumn(DatabaseConstants.ExceptionMessage, "varchar(max)");

        AddColumn<DateTimeOffset>(DatabaseConstants.SentAt);
        AddColumn<bool>(DatabaseConstants.Replayable);
        
        if (durability.DeadLetterQueueExpirationEnabled)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Expires).AllowNulls();
        }
    }
}