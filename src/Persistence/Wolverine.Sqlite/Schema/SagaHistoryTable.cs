using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

/// <summary>
/// GH-5665. See <see cref="DatabaseConstants.SagaHistoryTable"/>. <c>recorded_at</c> is sortable UTC text, as the
/// saga tables' timestamps are
/// </summary>
internal class SagaHistoryTable : Table
{
    public SagaHistoryTable(string schemaName) : base(
        new SqliteObjectName(TablePrefixing.Apply(schemaName, DatabaseConstants.SagaHistoryTable)))
    {
        var tableName = TablePrefixing.Apply(schemaName, DatabaseConstants.SagaHistoryTable);

        AddColumn(DatabaseConstants.Id, "TEXT").AsPrimaryKey();
        AddColumn(DatabaseConstants.SagaType, "TEXT").NotNull();
        AddColumn(DatabaseConstants.SagaId, "TEXT").NotNull();
        AddColumn(DatabaseConstants.SagaHistoryTenantId, "TEXT");
        AddColumn(DatabaseConstants.MessageId, "TEXT");
        AddColumn(DatabaseConstants.MessageType, "TEXT").NotNull();
        AddColumn(DatabaseConstants.Handler, "TEXT").NotNull();
        AddColumn(DatabaseConstants.Outcome, "TEXT").NotNull();
        AddColumn(DatabaseConstants.CompletesSaga, "INTEGER").NotNull();
        AddColumn(DatabaseConstants.RecordedAt, "TEXT").NotNull();
        AddColumn(DatabaseConstants.SentMessages, "TEXT");

        Indexes.Add(new IndexDefinition($"idx_{tableName}_saga")
        {
            Columns = [DatabaseConstants.SagaType, DatabaseConstants.SagaId]
        });
    }
}
