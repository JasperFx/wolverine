using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Weasel.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore;

public static class WolverineDbContextExtensions
{
    /// <summary>
    /// Ensures the database referenced by the DbContext's connection exists, creating it if necessary.
    /// TODO: Move this method to Weasel.EntityFrameworkCore.DbContextExtensions in a future Weasel release.
    /// </summary>
    public static async Task EnsureDatabaseExistsAsync(
        this IServiceProvider services,
        DbContext context,
        CancellationToken ct = default)
    {
        var (conn, migrator) = services.FindMigratorForDbContext(context);

        // When credentials are available in the connection string, use the standard Weasel path.
        // This is the common case for connection-string-based configurations.
        if (HasCredentials(conn.ConnectionString))
        {
            await migrator!.EnsureDatabaseExistsAsync(conn, ct).ConfigureAwait(false);
            return;
        }

        // When using a DbDataSource (e.g., UseNpgsql(dataSource)), the connection's
        // ConnectionString may strip credentials (Npgsql does this for security).
        // Try the DbContext's configured connection string which may preserve them.
        var dbContextConnStr = context.Database.GetConnectionString();
        if (dbContextConnStr != null && HasCredentials(dbContextConnStr))
        {
            await using var credConn = (DbConnection)Activator.CreateInstance(conn.GetType(), dbContextConnStr)!;
            await migrator!.EnsureDatabaseExistsAsync(credConn, ct).ConfigureAwait(false);
            return;
        }

        // Last resort for DataSource-based connections (e.g., Marten multi-tenancy with
        // NpgsqlDataSource): the data source manages authentication internally, so credentials
        // aren't exposed in the connection string. Use EF Core's IRelationalDatabaseCreator
        // which has internal access to the data source and can create databases.
        var creator = context.Database.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct).ConfigureAwait(false))
        {
            await creator.CreateAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool HasCredentials(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return false;

        return connectionString.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Pwd", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Integrated Security", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Trusted_Connection", StringComparison.OrdinalIgnoreCase);
    }
}
