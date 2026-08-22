using Microsoft.Data.SqlClient;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.SqlServer;

/// <summary>
/// Extension methods for configuring a SQL Server database-LOB backed
/// <see cref="IClaimCheckStore"/> from a Wolverine <see cref="ClaimCheckConfiguration"/>.
/// </summary>
public static class SqlServerClaimCheckExtensions
{
    /// <summary>
    /// Use a SQL Server <c>varbinary(max)</c> table in the database reached by
    /// <paramref name="connectionString"/> as the backing store for Wolverine claim checks. The schema and
    /// table are created on first use, and the store supports Wolverine-driven payload expiration.
    /// </summary>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="connectionString">Connection string for the target SQL Server database.</param>
    /// <param name="schemaName">Schema that owns the claim check table.</param>
    /// <param name="tableName">Name of the claim check table.</param>
    public static ClaimCheckConfiguration UseSqlServerClaimCheck(
        this ClaimCheckConfiguration config,
        string connectionString,
        string schemaName = SqlServerClaimCheckStore.DefaultSchemaName,
        string tableName = SqlServerClaimCheckStore.DefaultTableName)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.Store = new SqlServerClaimCheckStore(connectionString, schemaName, tableName);
        return config;
    }

    /// <summary>
    /// Use a SQL Server <c>varbinary(max)</c> table as the backing store for Wolverine claim checks,
    /// opening connections through <paramref name="connectionSource"/>. Use this overload when connections
    /// need custom construction, for example an access-token credential.
    /// </summary>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="connectionSource">Opens a connection to the target SQL Server database.</param>
    /// <param name="schemaName">Schema that owns the claim check table.</param>
    /// <param name="tableName">Name of the claim check table.</param>
    public static ClaimCheckConfiguration UseSqlServerClaimCheck(
        this ClaimCheckConfiguration config,
        Func<CancellationToken, Task<SqlConnection>> connectionSource,
        string schemaName = SqlServerClaimCheckStore.DefaultSchemaName,
        string tableName = SqlServerClaimCheckStore.DefaultTableName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(connectionSource);

        config.Store = new SqlServerClaimCheckStore(connectionSource, schemaName, tableName);
        return config;
    }
}
