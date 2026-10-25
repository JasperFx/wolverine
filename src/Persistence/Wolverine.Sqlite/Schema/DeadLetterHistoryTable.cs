using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

/// <summary>
/// GH-5209. See <see cref="DatabaseConstants.DeadLetterHistoryTable"/>
/// </summary>
internal class DeadLetterHistoryTable : Table
{
    public DeadLetterHistoryTable(string schemaName) : base(
        new SqliteObjectName(TablePrefixing.Apply(schemaName, DatabaseConstants.DeadLetterHistoryTable)))
    {
        var tableName = TablePrefixing.Apply(schemaName, DatabaseConstants.DeadLetterHistoryTable);

        AddColumn(DatabaseConstants.Id, "TEXT").AsPrimaryKey();
        AddColumn(DatabaseConstants.ReplayCount, "INTEGER");
        AddColumn(DatabaseConstants.LastReplayedAt, "TEXT");
        AddColumn(DatabaseConstants.FirstFailedAt, "TEXT");

        Indexes.Add(new IndexDefinition($"idx_{tableName}_{DatabaseConstants.LastReplayedAt}")
        {
            Columns = [DatabaseConstants.LastReplayedAt]
        });
    }
}
