using Weasel.Core;
using Weasel.SqlServer.Tables;

namespace Wolverine.SqlServer.Schema;

internal static class RateLimitTableColumns
{
    public const string Key = "rate_limit_key";
    public const string WindowStart = "window_start";
    public const string WindowEnd = "window_end";
    public const string Limit = "limit_per_window";
    public const string CurrentCount = "current_count";
}

internal class RateLimitTable : Table
{
    public RateLimitTable(string schemaName, string tableName) : base(new DbObjectName(schemaName, tableName))
    {
        AddColumn(RateLimitTableColumns.Key, "varchar(500)").NotNull().AsPrimaryKey();
        AddColumn<DateTimeOffset>(RateLimitTableColumns.WindowStart).NotNull().AsPrimaryKey();
        AddColumn<DateTimeOffset>(RateLimitTableColumns.WindowEnd).NotNull();
        AddColumn<int>(RateLimitTableColumns.Limit).NotNull();
        AddColumn<int>(RateLimitTableColumns.CurrentCount).NotNull();
    }
}
