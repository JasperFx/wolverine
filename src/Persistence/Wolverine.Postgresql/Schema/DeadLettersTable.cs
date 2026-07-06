using Weasel.Core;
using Weasel.Postgresql;
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

        // GH-3279: the durability agent's DLQ replay (INSERT ... SELECT ... WHERE replayable = $1)
        // and cleanup DELETE both filter on `replayable`. Without an index this seq-scans the whole
        // dead-letter table on every replay cycle — a multi-second, multi-GB scan for the handful of
        // replayable rows. A partial index scoped to replayable = true stays tiny (replayed rows are
        // deleted immediately) and turns both statements into index lookups.
        Indexes.Add(new IndexDefinition(PostgresqlIdentifier.Shorten($"idx_{DatabaseConstants.DeadLetterTable}_replayable"))
        {
            Columns = [DatabaseConstants.Replayable],
            Predicate = $"{DatabaseConstants.Replayable} = true"
        });

        if (durability.DeadLetterQueueExpirationEnabled)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Expires).AllowNulls();

            // Same story for the expiration sweep (DeleteExpiredDeadLetterMessagesOperation) which
            // filters on `expires`. Only present when DLQ expiration is enabled, so the index is too.
            Indexes.Add(new IndexDefinition(PostgresqlIdentifier.Shorten($"idx_{DatabaseConstants.DeadLetterTable}_expires"))
            {
                Columns = [DatabaseConstants.Expires],
                Predicate = $"{DatabaseConstants.Expires} is not null"
            });
        }
    }
}