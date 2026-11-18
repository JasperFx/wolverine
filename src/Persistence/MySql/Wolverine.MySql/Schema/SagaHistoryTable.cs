using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Schema;

/// <summary>
/// GH-5665. See <see cref="DatabaseConstants.SagaHistoryTable"/>
/// </summary>
internal class SagaHistoryTable : Table
{
    public SagaHistoryTable(string schemaName) : base(new DbObjectName(schemaName, DatabaseConstants.SagaHistoryTable))
    {
        AddColumn(DatabaseConstants.Id, "varchar(36)").NotNull().AsPrimaryKey();
        AddColumn(DatabaseConstants.SagaType, "varchar(255)").NotNull();
        AddColumn(DatabaseConstants.SagaId, "varchar(255)").NotNull();
        AddColumn(DatabaseConstants.SagaHistoryTenantId, "varchar(255)").AllowNulls();
        AddColumn(DatabaseConstants.MessageId, "varchar(36)").AllowNulls();
        AddColumn(DatabaseConstants.MessageType, "varchar(500)").NotNull();
        AddColumn(DatabaseConstants.Handler, "varchar(500)").NotNull();
        AddColumn(DatabaseConstants.Outcome, "varchar(25)").NotNull();
        AddColumn<bool>(DatabaseConstants.CompletesSaga).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.RecordedAt).NotNull();
        AddColumn(DatabaseConstants.SentMessages, "longtext").AllowNulls();

        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.SagaHistoryTable}_saga")
        {
            Columns = [DatabaseConstants.SagaType, DatabaseConstants.SagaId]
        });
    }
}
