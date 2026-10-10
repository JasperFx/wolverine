using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;

namespace Wolverine.RDBMS.MultiTenancy;

/// <summary>
///     A managed table that exists in the database as a plain, unpartitioned table even though
///     PartitionPerTenant() expects it to be partitioned -- the shape PartitionPerTenant() meets
///     when it is switched on for a conjoined DbContext that already has tables
/// </summary>
/// <param name="Identifier">The table</param>
/// <param name="RowCount">How many rows it holds; zero means it can simply be recreated</param>
/// <param name="TenantIds">
///     Every distinct tenant id found in its rows. These are the tenants that need a partition
///     before the rows can be reloaded into a partitioned table
/// </param>
public record UnpartitionedTable(DbObjectName Identifier, long RowCount, IReadOnlyList<string> TenantIds);

/// <summary>
///     GH-3541. Engine-specific support for turning PartitionPerTenant() on over an EXISTING
///     table. Neither PostgreSQL nor SQL Server can convert a plain table to a partitioned one in
///     place, so the only route is a rebuild: copy the rows aside, drop the table, recreate it
///     partitioned from the same Weasel definition that creates it on a fresh database, reload the
///     rows, and drop the copy -- in one transaction
/// </summary>
public interface ITenantPartitionRebuilder
{
    /// <summary>
    ///     Every managed table that exists in the database but is not partitioned, with its row
    ///     count and the tenant ids its rows carry
    /// </summary>
    Task<IReadOnlyList<UnpartitionedTable>> FindUnpartitionedTablesAsync(IDatabaseWithTables database,
        CancellationToken token);

    /// <summary>
    ///     The DDL that rebuilds one table as partitioned, for review or for applying by hand. The
    ///     partitions it creates are the ones currently registered, so every tenant found in the
    ///     table's rows must be registered BEFORE this is written or the reload will fail
    /// </summary>
    string WriteRebuildScript(IDatabaseWithTables database, UnpartitionedTable table);

    /// <summary>
    ///     Execute the rebuild script for one table inside a single transaction. Same registration
    ///     precondition as <see cref="WriteRebuildScript" />
    /// </summary>
    Task RebuildAsync(ILogger logger, IDatabaseWithTables database, UnpartitionedTable table,
        CancellationToken token);

    /// <summary>
    ///     Drop a plain table that holds no rows, so the next migration simply recreates it
    ///     partitioned
    /// </summary>
    Task DropEmptyTableAsync(IDatabaseWithTables database, UnpartitionedTable table, CancellationToken token);
}

/// <summary>
///     GH-3541. Thrown when PartitionPerTenant() meets a managed table that already exists as a
///     plain, unpartitioned table holding rows. Raised deliberately, before any DDL is attempted,
///     instead of letting the partition DDL fail against the plain table with a raw engine error:
///     Wolverine will not rebuild a table that holds data as a side effect of a migration or of
///     registering a tenant
/// </summary>
public class UnpartitionedTenantTableException : Exception
{
    public UnpartitionedTenantTableException(Type dbContextType, IReadOnlyList<UnpartitionedTable> tables)
        : base(buildMessage(dbContextType, tables))
    {
        DbContextType = dbContextType;
        Tables = tables;
    }

    public Type DbContextType { get; }

    /// <summary>
    ///     The plain tables, with their row counts and the tenants their rows belong to
    /// </summary>
    public IReadOnlyList<UnpartitionedTable> Tables { get; }

    private static string buildMessage(Type dbContextType, IReadOnlyList<UnpartitionedTable> tables)
    {
        var lines = tables.Select(x =>
            $"  {x.Identifier.QualifiedName} ({x.RowCount} rows across tenants {string.Join(", ", x.TenantIds.OrderBy(t => t))})");

        return
            $"PartitionPerTenant() is enabled for {dbContextType.Name}, but these tables already exist as plain, unpartitioned tables and hold rows:{Environment.NewLine}" +
            string.Join(Environment.NewLine, lines) + Environment.NewLine +
            "A plain table cannot be converted to a partitioned one in place on PostgreSQL or SQL Server, and Wolverine will not rebuild a table " +
            "holding data as a side effect of a migration or of registering a tenant. Either call " +
            $"IConjoinedTenantPartitions<{dbContextType.Name}>.RebuildUnpartitionedTablesAsync() to rebuild each table in one transaction " +
            "(copy the rows aside, drop, recreate partitioned, reload, drop the copy), or WriteRebuildScriptAsync() to get that DDL to review " +
            "and apply yourself. See 'Enabling partitioning on an existing table' in the EF Core multi-tenancy documentation.";
    }
}
