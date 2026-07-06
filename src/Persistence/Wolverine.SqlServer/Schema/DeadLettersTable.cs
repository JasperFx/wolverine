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

        // GH-3279: the durability agent's DLQ replay (INSERT ... SELECT ... WHERE replayable = @p)
        // and cleanup DELETE both filter on `replayable`. Without an index this scans the whole
        // dead-letter table on every replay cycle. A filtered index scoped to replayable stays tiny
        // (replayed rows are deleted immediately) and turns both statements into index seeks.
        // The predicate uses SqlServer's canonical filter form ([col]=val — bracketed, no spaces)
        // so it round-trips through sys.indexes.filter_definition instead of thrashing (drop+recreate)
        // on every schema migration.
        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeadLetterTable}_replayable")
        {
            Columns = [DatabaseConstants.Replayable],
            Predicate = $"[{DatabaseConstants.Replayable}]=1"
        });

        if (durability.DeadLetterQueueExpirationEnabled)
        {
            AddColumn<DateTimeOffset>(DatabaseConstants.Expires).AllowNulls();

            // Same story for the expiration sweep (DeleteExpiredDeadLetterMessagesOperation) which
            // filters on `expires`. Only present when DLQ expiration is enabled, so the index is too.
            Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeadLetterTable}_expires")
            {
                Columns = [DatabaseConstants.Expires],
                Predicate = $"[{DatabaseConstants.Expires}] IS NOT NULL"
            });
        }
    }
}