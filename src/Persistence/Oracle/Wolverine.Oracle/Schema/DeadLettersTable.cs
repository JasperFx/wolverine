using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Oracle.Schema;

internal class DeadLettersTable : Table
{
    public DeadLettersTable(DurabilitySettings durability, string schemaName) : base(
        new OracleObjectName(schemaName.ToUpperInvariant(), DatabaseConstants.DeadLetterTable.ToUpperInvariant()))
    {
        AddColumn<Guid>(DatabaseConstants.Id).NotNull().AsPrimaryKey();

        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime);
        AddColumn(DatabaseConstants.Body, "BLOB").NotNull();

        AddColumn(DatabaseConstants.MessageType, "VARCHAR2(500)").NotNull();

        if (durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            AddColumn(DatabaseConstants.ReceivedAt, "VARCHAR2(500)").NotNull();
        }
        else
        {
            AddColumn(DatabaseConstants.ReceivedAt, "VARCHAR2(250)").AsPrimaryKey().NotNull();
        }

        AddColumn(DatabaseConstants.Source, "VARCHAR2(500)");
        AddColumn(DatabaseConstants.ExceptionType, "VARCHAR2(4000)");
        AddColumn(DatabaseConstants.ExceptionMessage, "VARCHAR2(4000)");

        AddColumn<DateTimeOffset>(DatabaseConstants.SentAt);
        AddColumn<bool>(DatabaseConstants.Replayable);

        // GH-3279: DLQ replay and cleanup both filter on `replayable`. This is a plain b-tree index
        // (WHERE-clause partial indexes are Oracle 23c+ only), which still turns the durability
        // agent's replay-cycle full scan into an index lookup.
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
