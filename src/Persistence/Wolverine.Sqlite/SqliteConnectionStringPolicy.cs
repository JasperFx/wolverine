using System.Data.Common;
using JasperFx.Core;
using Microsoft.Data.Sqlite;

namespace Wolverine.Sqlite;

internal static class SqliteConnectionStringPolicy
{
    public static void AssertFileBased(string? connectionString, string context)
    {
        if (connectionString.IsEmpty())
        {
            throw new InvalidOperationException(
                $"SQLite {context} is required and must point to a file-based database.");
        }

        if (isInMemory(connectionString!))
        {
            throw new InvalidOperationException(
                $"SQLite in-memory databases are not supported for Wolverine durability. Configure a file-based SQLite connection string for {context} (for example 'Data Source=wolverine.db').");
        }
    }

    public static void AssertFileBased(DbDataSource dataSource, string context)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        AssertFileBased(dataSource.ConnectionString, $"{context}.ConnectionString");
    }

    private static bool isInMemory(string connectionString)
    {
        var raw = connectionString.Trim();
        if (raw.IndexOf("mode=memory", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (raw.IndexOf("data source=:memory:", StringComparison.OrdinalIgnoreCase) >= 0 ||
            raw.IndexOf("filename=:memory:", StringComparison.OrdinalIgnoreCase) >= 0 ||
            raw.IndexOf(":memory:", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource?.Trim();

            if (builder.Mode == SqliteOpenMode.Memory)
            {
                return true;
            }

            if (dataSource.IsEmpty())
            {
                return false;
            }

            if (dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (dataSource.IndexOf(":memory:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                   && dataSource.IndexOf("mode=memory", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            // Fall back to raw-string checks above.
            return false;
        }
    }
}
