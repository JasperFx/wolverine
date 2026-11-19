using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

/// <summary>
/// GH-5665. See <see cref="DatabaseConstants.SagaHistoryTable"/>. The key columns are nvarchar because SqlClient binds a
/// string as nvarchar, and a varchar column would not be seeked by its index
/// </summary>
internal class SagaHistoryTable : Table
{
    public SagaHistoryTable(string schemaName) : base(new DbObjectName(schemaName, DatabaseConstants.SagaHistoryTable))
    {
        AddColumn(DatabaseConstants.Id, "nvarchar(36)").NotNull().AsPrimaryKey();
        AddColumn(DatabaseConstants.SagaType, "nvarchar(400)").NotNull();
        AddColumn(DatabaseConstants.SagaId, "nvarchar(200)").NotNull();
        AddColumn(DatabaseConstants.SagaHistoryTenantId, "nvarchar(200)").AllowNulls();
        AddColumn(DatabaseConstants.MessageId, "nvarchar(36)").AllowNulls();
        AddColumn(DatabaseConstants.MessageType, "nvarchar(500)").NotNull();
        AddColumn(DatabaseConstants.Handler, "nvarchar(500)").NotNull();
        AddColumn(DatabaseConstants.Outcome, "nvarchar(25)").NotNull();
        AddColumn<bool>(DatabaseConstants.CompletesSaga).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.RecordedAt).NotNull();
        AddColumn(DatabaseConstants.SentMessages, "nvarchar(max)").AllowNulls();

        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.SagaHistoryTable}_saga")
        {
            Columns = [DatabaseConstants.SagaType, DatabaseConstants.SagaId]
        });
    }
}
