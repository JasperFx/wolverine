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
    ///     Create the partition for a new tenant across every partitioned table
    /// </summary>
    Task AddTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Create or join a partition for a tenant. Tenants registered with the same
    ///     partition suffix share one physical partition ("bucketing") when
    ///     AllowPartitionSharing is enabled on the partitioning options
    /// </summary>
    Task AddTenantAsync(string tenantId, string partitionSuffix, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Batch registration of tenants; the dictionary value is the optional
    ///     partition suffix (null = own partition per tenant)
    /// </summary>
    Task AddTenantsAsync(IReadOnlyDictionary<string, string?> tenantIdToSuffix,
        CancellationToken cancellationToken = default);

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

    internal EfSchemaMappingCustomization BuildCustomization()
    {
        var partitioning = Partitioning;
        return new EfSchemaMappingCustomization
        {
            AdditionalObjects = partitioning.AdditionalSchemaObjects,
            CustomizeTable = (entityType, table) =>
            {
                if (ConjoinedTenancy.IsPartitionedEntity(entityType))
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

        var builder = _serviceProvider.GetRequiredService<IDbContextBuilder<T>>();
        await using var context = await builder.BuildAsync(cancellationToken);
        var database = _serviceProvider.CreateDatabase(context, BuildCustomization(),
            "conjoined:" + typeof(T).FullNameInCode());
        Partitioning.AttachInitializer(database);
        _database = database;
        return database;
    }

    public async Task AddTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        await AddTenantsAsync(new Dictionary<string, string?> { [tenantId] = null }, cancellationToken);
    }

    public Task AddTenantAsync(string tenantId, string partitionSuffix,
        CancellationToken cancellationToken = default)
    {
        return AddTenantsAsync(new Dictionary<string, string?> { [tenantId] = partitionSuffix }, cancellationToken);
    }

    public async Task AddTenantsAsync(IReadOnlyDictionary<string, string?> tenantIdToSuffix,
        CancellationToken cancellationToken = default)
    {
        var database = await BuildWeaselDatabaseAsync(cancellationToken);
        await Partitioning.AddTenantsAsync(_logger, database, tenantIdToSuffix, cancellationToken);
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
