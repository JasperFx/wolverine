using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Schema;

internal class DeadLettersTable : Table
{
    public DeadLettersTable(DurabilitySettings durability, string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.DeadLetterTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).NotNull().AsPrimaryKey();

        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
        AddColumn(DatabaseConstants.Body, "longblob").NotNull();

        AddColumn(DatabaseConstants.MessageType, "varchar(500)").NotNull();

        if (durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            AddColumn(DatabaseConstants.ReceivedAt, "varchar(500)").NotNull();
        }
        else
        {
            AddColumn(DatabaseConstants.ReceivedAt, "varchar(250)").AsPrimaryKey().NotNull();
        }

        AddColumn(DatabaseConstants.Source, "varchar(500)");
        AddColumn(DatabaseConstants.ExceptionType, "text");
        AddColumn(DatabaseConstants.ExceptionMessage, "text");

        AddColumn<DateTimeOffset>(DatabaseConstants.SentAt);
        AddColumn<bool>(DatabaseConstants.Replayable);

        if (durability.DeadLetterQueueExpirationEnabled)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Expires).AllowNulls();
        }
    }
}
