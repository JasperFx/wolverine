using Npgsql;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Postgresql;

/// <summary>
/// Extension methods for configuring a PostgreSQL database-LOB backed
/// <see cref="IClaimCheckStore"/> from a Wolverine <see cref="ClaimCheckConfiguration"/>.
/// </summary>
public static class PostgresqlClaimCheckExtensions
{
    /// <summary>
    /// Use a PostgreSQL <c>bytea</c> table in the database reached by <paramref name="dataSource"/>
    /// as the backing store for Wolverine claim checks. The table is created on first use.
    /// </summary>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="dataSource">Data source for the target PostgreSQL database.</param>
    /// <param name="schemaName">Schema that owns the claim check table.</param>
    /// <param name="tableName">Name of the claim check table.</param>
    public static ClaimCheckConfiguration UsePostgresqlClaimCheck(
        this ClaimCheckConfiguration config,
        NpgsqlDataSource dataSource,
        string schemaName = PostgresqlClaimCheckStore.DefaultSchemaName,
        string tableName = PostgresqlClaimCheckStore.DefaultTableName)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (dataSource is null) throw new ArgumentNullException(nameof(dataSource));

        config.Store = new PostgresqlClaimCheckStore(dataSource, schemaName, tableName);
        return config;
    }

    /// <summary>
    /// Use a PostgreSQL <c>bytea</c> table in the database reached by <paramref name="connectionString"/>
    /// as the backing store for Wolverine claim checks. A dedicated <see cref="NpgsqlDataSource"/> is
    /// built from the connection string. The table is created on first use.
    /// </summary>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="connectionString">Connection string for the target PostgreSQL database.</param>
    /// <param name="schemaName">Schema that owns the claim check table.</param>
    /// <param name="tableName">Name of the claim check table.</param>
    public static ClaimCheckConfiguration UsePostgresqlClaimCheck(
        this ClaimCheckConfiguration config,
        string connectionString,
        string schemaName = PostgresqlClaimCheckStore.DefaultSchemaName,
        string tableName = PostgresqlClaimCheckStore.DefaultTableName)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string must be provided", nameof(connectionString));
        }

        var dataSource = NpgsqlDataSource.Create(connectionString);
        config.Store = new PostgresqlClaimCheckStore(dataSource, schemaName, tableName);
        return config;
    }
}
