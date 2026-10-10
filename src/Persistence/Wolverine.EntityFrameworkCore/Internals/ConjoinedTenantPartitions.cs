using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.EntityFrameworkCore;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     Management API for the Weasel-managed tenant partitions behind a
///     conjoined multi-tenant DbContext registered with PartitionPerTenant()
/// </summary>
// ReSharper disable once UnusedTypeParameter -- T scopes the service to its DbContext registration
public interface IConjoinedTenantPartitions<T> where T : DbContext
{
    /// <summary>
    ///     Create the partition for a new tenant across every partitioned table.
    ///     The returned result reports the outcome per table -- partition DDL is
    ///     applied with failures isolated, so check Succeeded rather than assuming
    ///     an exception-free call reached every table
    /// </summary>
    Task<TenantPartitionResult> AddTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Create or join a partition for a tenant. Tenants registered with the same
    ///     partition suffix share one physical partition ("bucketing") when
    ///     AllowPartitionSharing is enabled on the partitioning options
    /// </summary>
    Task<TenantPartitionResult> AddTenantAsync(string tenantId, string partitionSuffix,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Batch registration of tenants; the dictionary value is the optional
    ///     partition suffix (null = own partition per tenant)
    /// </summary>
    Task<TenantPartitionResult> AddTenantsAsync(IReadOnlyDictionary<string, string?> tenantIdToSuffix,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Back-fill: reconcile every partitioned table against the full registered
    ///     tenant set. Call this when a table joins an existing managed set -- a
    ///     newly deployed service, or a newly mapped ITenanted entity -- because
    ///     routine migrations deliberately leave managed partitions alone and the
    ///     new table would otherwise have no partition for any existing tenant
    /// </summary>
    Task<TenantPartitionResult> MigrateTenantPartitionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Remove a tenant from the partition set. On PostgreSQL this detaches and
    ///     drops the tenant's partition (deleteData must be true); on SQL Server
    ///     deleteData: false retains the rows
    /// </summary>
    Task DropTenantAsync(string tenantId, bool deleteData = false, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Hydrate the in-memory tenant partition map from the control table
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     GH-3541. Every managed table that already exists as a plain, unpartitioned table -- what
    ///     PartitionPerTenant() meets when it is switched on for a DbContext that has tables with data.
    ///     Each entry carries the row count and the tenant ids found in the rows. Empty means every
    ///     managed table is either partitioned or not created yet
    /// </summary>
    Task<IReadOnlyList<UnpartitionedTable>> FindUnpartitionedTablesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     GH-3541. Rebuild every plain managed table as a partitioned one, keeping its rows: each
    ///     table is copied aside, dropped, recreated partitioned from the same definition that creates
    ///     it on a fresh database, reloaded, and the copy dropped -- all in one transaction per table.
    ///     Every tenant id found in the rows is registered first (additive and idempotent, like
    ///     AddTenantAsync) so the rows have a partition to land in. Run this while the application is
    ///     not writing to those tables. Returns the qualified names of the tables rebuilt
    /// </summary>
    Task<IReadOnlyList<string>> RebuildUnpartitionedTablesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     GH-3541. The DDL that RebuildUnpartitionedTablesAsync() would execute, to review or apply by
    ///     hand -- one transaction-sized script per plain table, concatenated. Like the rebuild, this
    ///     registers every tenant id found in the rows first, so the script's partitions match the data
    /// </summary>
    Task<string> WriteRebuildScriptAsync(CancellationToken cancellationToken = default);
}

internal class ConjoinedTenantPartitions<T> : IConjoinedTenantPartitions<T> where T : DbContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConjoinedTenantPartitions<T>> _logger;
    private readonly object _locker = new();
    private ITenantPartitioning? _partitioning;
    private IDatabaseWithTables? _database;

    public ConjoinedTenantPartitions(IServiceProvider serviceProvider,
        ILogger<ConjoinedTenantPartitions<T>> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    internal ITenantPartitioning Partitioning
    {
        get
        {
            if (_partitioning != null)
            {
                return _partitioning;
            }

            lock (_locker)
            {
                if (_partitioning != null)
                {
                    return _partitioning;
                }

                var options = ConjoinedTenancy.OptionsFor(typeof(T));
                if (!options.PartitioningEnabled)
                {
                    throw new InvalidOperationException(
                        $"DbContext type {typeof(T).FullNameInCode()} is not registered with PartitionPerTenant()");
                }

                var builder = _serviceProvider.GetRequiredService<IDbContextBuilder<T>>();
                using var context = builder.BuildForMain();
                var providerName = context.Database.ProviderName ?? string.Empty;

                var factory = _serviceProvider.GetServices<ITenantPartitioningProviderFactory>()
                    .FirstOrDefault(x => x.MatchesEfCoreProvider(providerName));

                if (factory == null)
                {
                    throw new InvalidOperationException(
                        $"No tenant partitioning support is registered for EF Core provider '{providerName}'. Wolverine supplies partitioning for PostgreSQL and SQL Server through their message persistence packages.");
                }

                var settings = _serviceProvider.GetRequiredService<DatabaseSettings>();
                var controlTable = new DbObjectName(settings.SchemaName ?? "public",
                    options.PartitionControlTableName);

                _partitioning = factory.Create(controlTable, options.Partitioning!);
                ConjoinedTenancy.RegisterPartitioning(typeof(T), _partitioning);
                return _partitioning;
            }
        }
    }

    internal EfSchemaMappingCustomization BuildCustomization(ISet<string>? leaveUnpartitioned = null)
    {
        var partitioning = Partitioning;
        return new EfSchemaMappingCustomization
        {
            AdditionalObjects = partitioning.AdditionalSchemaObjects,
            CustomizeTable = (entityType, table) =>
            {
                if (ConjoinedTenancy.IsPartitionedEntity(entityType)
                    && leaveUnpartitioned?.Contains(table.Identifier.QualifiedName) != true)
                {
                    partitioning.ApplyToTable(table);
                }
            }
        };
    }

    internal async ValueTask<IDatabaseWithTables> BuildWeaselDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_database != null)
        {
            return _database;
        }

        var database = await buildWeaselDatabaseAsync(null, cancellationToken);
        _database = database;
        return database;
    }

    private async ValueTask<IDatabaseWithTables> buildWeaselDatabaseAsync(ISet<string>? leaveUnpartitioned,
        CancellationToken cancellationToken)
    {
        var builder = _serviceProvider.GetRequiredService<IDbContextBuilder<T>>();
        await using var context = await builder.BuildAsync(cancellationToken);
        var database = _serviceProvider.CreateDatabase(context, BuildCustomization(leaveUnpartitioned),
            "conjoined:" + typeof(T).FullNameInCode());
        Partitioning.AttachInitializer(database);
        return database;
    }

    public Task<TenantPartitionResult> AddTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        return AddTenantsAsync(new Dictionary<string, string?> { [tenantId] = null }, cancellationToken);
    }

    public Task<TenantPartitionResult> AddTenantAsync(string tenantId, string partitionSuffix,
        CancellationToken cancellationToken = default)
    {
        return AddTenantsAsync(new Dictionary<string, string?> { [tenantId] = partitionSuffix }, cancellationToken);
    }

    public async Task<TenantPartitionResult> AddTenantsAsync(IReadOnlyDictionary<string, string?> tenantIdToSuffix,
        CancellationToken cancellationToken = default)
    {
        var database = await BuildWeaselDatabaseAsync(cancellationToken);
        await assertNoUnpartitionedTablesAsync(database, cancellationToken);
        return await Partitioning.AddTenantsAsync(_logger, database, tenantIdToSuffix, cancellationToken);
    }

    public async Task<TenantPartitionResult> MigrateTenantPartitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var database = await BuildWeaselDatabaseAsync(cancellationToken);
        await assertNoUnpartitionedTablesAsync(database, cancellationToken);
        return await Partitioning.MigrateAllTablesAsync(_logger, database, cancellationToken);
    }

    // ---- GH-3541: enabling partitioning over an existing table -------------------------------

    private ITenantPartitionRebuilder rebuilder =>
        Partitioning as ITenantPartitionRebuilder
        ?? throw new NotSupportedException(
            $"The tenant partitioning for {typeof(T).FullNameInCode()} ({Partitioning.GetType().FullNameInCode()}) does not support rebuilding existing tables");

    /// <summary>
    ///     GH-3541. The partition DDL for a tenant fails against a plain table with a raw engine error
    ///     -- "is not partitioned" on PostgreSQL, a failed SPLIT on SQL Server -- long after the cause.
    ///     Refuse up front, naming the tables and the way out
    /// </summary>
    private async Task assertNoUnpartitionedTablesAsync(IDatabaseWithTables database, CancellationToken cancellationToken)
    {
        var plain = await rebuilder.FindUnpartitionedTablesAsync(database, cancellationToken);
        if (plain.Count > 0)
        {
            throw new UnpartitionedTenantTableException(typeof(T), plain);
        }
    }

    public async Task<IReadOnlyList<UnpartitionedTable>> FindUnpartitionedTablesAsync(
        CancellationToken cancellationToken = default)
    {
        var database = await BuildWeaselDatabaseAsync(cancellationToken);
        return await rebuilder.FindUnpartitionedTablesAsync(database, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> RebuildUnpartitionedTablesAsync(CancellationToken cancellationToken = default)
    {
        var database = await BuildWeaselDatabaseAsync(cancellationToken);
        var plain = await rebuilder.FindUnpartitionedTablesAsync(database, cancellationToken);
        if (plain.Count == 0)
        {
            return [];
        }

        await registerTenantsFoundInRowsAsync(plain, cancellationToken);

        var rebuilt = new List<string>();
        foreach (var table in plain)
        {
            await rebuilder.RebuildAsync(_logger, database, table, cancellationToken);
            rebuilt.Add(table.Identifier.QualifiedName);
        }

        return rebuilt;
    }

    public async Task<string> WriteRebuildScriptAsync(CancellationToken cancellationToken = default)
    {
        var database = await BuildWeaselDatabaseAsync(cancellationToken);
        var plain = await rebuilder.FindUnpartitionedTablesAsync(database, cancellationToken);
        if (plain.Count == 0)
        {
            return string.Empty;
        }

        await registerTenantsFoundInRowsAsync(plain, cancellationToken);

        var script = new System.Text.StringBuilder();
        foreach (var table in plain)
        {
            script.AppendLine(rebuilder.WriteRebuildScript(database, table));
            script.AppendLine();
        }

        return script.ToString();
    }

    /// <summary>
    ///     Every tenant whose rows sit in a plain table needs a partition before those rows can be
    ///     reloaded. Registering goes through the ordinary add-tenant path so the registry, the
    ///     in-memory map and the partitions of every table that IS already partitioned all move
    ///     together -- but that path runs partition DDL against every managed table, and would fail
    ///     against the very tables being rebuilt. So it runs against a view of the schema in which
    ///     those tables are, for the moment, not managed
    /// </summary>
    private async Task registerTenantsFoundInRowsAsync(IReadOnlyList<UnpartitionedTable> plain,
        CancellationToken cancellationToken)
    {
        var tenantIds = plain.SelectMany(x => x.TenantIds).Distinct().ToArray();
        if (tenantIds.Length == 0)
        {
            return;
        }

        var leaveAlone = plain.Select(x => x.Identifier.QualifiedName).ToHashSet();
        var others = await buildWeaselDatabaseAsync(leaveAlone, cancellationToken);

        var result = await Partitioning.AddTenantsAsync(_logger, others,
            tenantIds.ToDictionary(x => x, _ => (string?)null), cancellationToken);

        if (!result.Succeeded)
        {
            throw new TenantPartitionException(result.Failures);
        }
    }

    /// <summary>
    ///     Called by the Wolverine-managed migration before Weasel computes its deltas. A plain table
    ///     holding rows is refused loudly (Weasel would otherwise try to rebuild it on PostgreSQL and
    ///     fail the reload with a raw error, and would leave it plain on SQL Server); an EMPTY plain
    ///     table is simply dropped so the migration recreates it partitioned
    /// </summary>
    internal async Task PrepareExistingTablesForMigrationAsync(CancellationToken cancellationToken)
    {
        var database = await BuildWeaselDatabaseAsync(cancellationToken);
        var plain = await rebuilder.FindUnpartitionedTablesAsync(database, cancellationToken);
        if (plain.Count == 0)
        {
            return;
        }

        var withRows = plain.Where(x => x.RowCount > 0).ToList();
        if (withRows.Count > 0)
        {
            throw new UnpartitionedTenantTableException(typeof(T), withRows);
        }

        foreach (var table in plain)
        {
            _logger.LogInformation(
                "Dropping the empty, unpartitioned table {Table} so the migration can recreate it partitioned per tenant for {DbContextType}",
                table.Identifier.QualifiedName, typeof(T).Name);
            await rebuilder.DropEmptyTableAsync(database, table, cancellationToken);
        }
    }

    public async Task DropTenantAsync(string tenantId, bool deleteData = false,
        CancellationToken cancellationToken = default)
    {
        var database = await BuildWeaselDatabaseAsync(cancellationToken);
        await Partitioning.DropTenantsAsync(_logger, database, [tenantId], deleteData, cancellationToken);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var database = await BuildWeaselDatabaseAsync(cancellationToken);
        await Partitioning.InitializeAsync(database, cancellationToken);
    }
}

/// <summary>
///     Hydrates the tenant partition map at host startup so the tenant ordinal
///     interceptor has a synchronous, pre-populated lookup before the first message
/// </summary>
internal class ConjoinedPartitionsActivator<T> : IHostedService where T : DbContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConjoinedTenantPartitions<T>> _logger;

    public ConjoinedPartitionsActivator(IServiceProvider serviceProvider,
        ILogger<ConjoinedTenantPartitions<T>> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _serviceProvider.GetRequiredService<IConjoinedTenantPartitions<T>>()
                .InitializeAsync(cancellationToken);
        }
        catch (Exception e)
        {
            // The control table may not exist yet on a brand new database when
            // resource setup runs later in the startup sequence; add-tenant and
            // migration paths re-initialize
            _logger.LogDebug(e,
                "Unable to pre-hydrate conjoined tenant partitions for {DbContextType}; the map hydrates on first use",
                typeof(T).Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
