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

        // GH-3279: DLQ replay and cleanup both filter on `replayable`. MySQL has no filtered/partial
        // indexes, so this is a plain index on the column — still far better than the full-table scan
        // the durability agent's replay cycle otherwise pays on every pass.
        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeadLetterTable}_replayable")
        {
            Columns = [DatabaseConstants.Replayable]
        });

        if (durability.DeadLetterQueueExpirationEnabled)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Expires).AllowNulls();

            // Same story for the expiration sweep, which filters on `expires`.
            Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeadLetterTable}_expires")
            {
                Columns = [DatabaseConstants.Expires]
            });
        }
    }
}
