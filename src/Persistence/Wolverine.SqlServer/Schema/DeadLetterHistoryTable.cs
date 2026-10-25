using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

/// <summary>
/// GH-5209. See <see cref="DatabaseConstants.DeadLetterHistoryTable"/>
/// </summary>
internal class DeadLetterHistoryTable : Table
{
    public DeadLetterHistoryTable(string schemaName) : base(new DbObjectName(schemaName, DatabaseConstants.DeadLetterHistoryTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).NotNull().AsPrimaryKey();
        AddColumn<int>(DatabaseConstants.ReplayCount).AllowNulls();
        AddColumn<DateTimeOffset>(DatabaseConstants.LastReplayedAt).AllowNulls();
        AddColumn<DateTimeOffset>(DatabaseConstants.FirstFailedAt).AllowNulls();

        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeadLetterHistoryTable}_{DatabaseConstants.LastReplayedAt}")
        {
            Columns = [DatabaseConstants.LastReplayedAt]
        });
    }
}
