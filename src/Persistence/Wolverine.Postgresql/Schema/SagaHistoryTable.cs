using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

/// <summary>
/// GH-5665. See <see cref="DatabaseConstants.SagaHistoryTable"/>
/// </summary>
internal class SagaHistoryTable : Table
{
    public SagaHistoryTable(string schemaName) : base(new DbObjectName(schemaName, DatabaseConstants.SagaHistoryTable))
    {
        AddColumn(DatabaseConstants.Id, "varchar(36)").AsPrimaryKey();
        AddColumn(DatabaseConstants.SagaType, "varchar").NotNull();
        AddColumn(DatabaseConstants.SagaId, "varchar").NotNull();
        AddColumn(DatabaseConstants.SagaHistoryTenantId, "varchar").AllowNulls();
        AddColumn(DatabaseConstants.MessageId, "varchar(36)").AllowNulls();
        AddColumn(DatabaseConstants.MessageType, "varchar").NotNull();
        AddColumn(DatabaseConstants.Handler, "varchar").NotNull();
        AddColumn(DatabaseConstants.Outcome, "varchar(25)").NotNull();
        AddColumn<bool>(DatabaseConstants.CompletesSaga).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.RecordedAt).NotNull();
        AddColumn(DatabaseConstants.SentMessages, "text").AllowNulls();

        Indexes.Add(new IndexDefinition(PostgresqlIdentifier.Shorten($"idx_{DatabaseConstants.SagaHistoryTable}_saga"))
        {
            Columns = [DatabaseConstants.SagaType, DatabaseConstants.SagaId]
        });
    }
}
