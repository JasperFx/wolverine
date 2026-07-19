using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     The authoritative tenant list for a conjoined multi-tenant DbContext, backed
///     by the message store's wolverine_tenants registry table. Registering this as
///     IDynamicTenantSource&lt;string&gt; lights up CritterWatch's tenant management --
///     add/disable/enable/remove fan out here. The "connection value" for every
///     tenant is the shared application database
/// </summary>
internal class ConjoinedTenantSource<T> : IDynamicTenantSource<string> where T : DbContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConjoinedTenantSource<T>> _logger;
    private readonly object _locker = new();
    private ITenantDatabaseRegistry? _registry;
    private ImHashMap<string, string> _active = ImHashMap<string, string>.Empty;

    public ConjoinedTenantSource(IServiceProvider serviceProvider, ILogger<ConjoinedTenantSource<T>> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public DatabaseCardinality Cardinality => DatabaseCardinality.Single;

    private WolverineOptions options => _serviceProvider.GetRequiredService<IWolverineRuntime>().Options;

    private ITenantDatabaseRegistry registry
    {
        get
        {
            if (_registry != null)
            {
                return _registry;
            }

            lock (_locker)
            {
                if (_registry != null)
                {
                    return _registry;
                }

                var store = _serviceProvider.GetRequiredService<IMessageStore>();
                if (store is not ITenantDatabaseRegistry tenantRegistry)
                {
                    throw new InvalidOperationException(
                        $"Conjoined tenancy for {typeof(T).FullNameInCode()} requires relational database message storage with the wolverine_tenants registry table");
                }

                _registry = tenantRegistry;
                return _registry;
            }
        }
    }

    private string sharedConnectionString
    {
        get
        {
            var database = (IMessageDatabase)_serviceProvider.GetRequiredService<IMessageStore>();
            return database.Settings.ConnectionString ?? database.Settings.DataSource?.ConnectionString ?? string.Empty;
        }
    }

    private IConjoinedTenantPartitions<T>? partitions => _serviceProvider.GetService<IConjoinedTenantPartitions<T>>();

    public ValueTask<string> FindAsync(string tenantId)
    {
        tenantId = options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        if (ConjoinedTenancy.IsTenantDisabled(typeof(T), tenantId))
        {
            throw new UnknownTenantIdException(tenantId);
        }

        // Every conjoined tenant shares the application database
        return new ValueTask<string>(sharedConnectionString);
    }

    public async Task RefreshAsync()
    {
        var assignments = await registry.LoadAllTenantConnectionStrings();
        var active = ImHashMap<string, string>.Empty;
        foreach (var assignment in assignments)
        {
            active = active.AddOrUpdate(assignment.TenantId, sharedConnectionString);
        }

        _active = active;

        var disabled = await registry.LoadDisabledTenantIdsAsync();
        ConjoinedTenancy.SetDisabledTenants(typeof(T), disabled);
    }

    public IReadOnlyList<string> AllActive()
    {
        return _active.Enumerate().Select(x => x.Value).Distinct().ToList();
    }

    public IReadOnlyList<Assignment<string>> AllActiveByTenant()
    {
        return _active.Enumerate().Select(x => new Assignment<string>(x.Key, x.Value)).ToList();
    }

    public Task AddTenantAsync(string tenantId, string connectionValue)
    {
        throw new InvalidOperationException(
            "Conjoined multi-tenancy uses a single, shared application database -- tenants cannot be assigned their own connection string. Use AddTenantAsync(tenantId) instead.");
    }

    public async Task<string> AddTenantAsync(string tenantId, CancellationToken token = default)
    {
        tenantId = options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);

        // Empty connection string marks a conjoined (shared database) tenant record
        await registry.AddTenantRecordAsync(tenantId, string.Empty);
        _active = _active.AddOrUpdate(tenantId, sharedConnectionString);
        ConjoinedTenancy.SetTenantDisabled(typeof(T), tenantId, false);

        if (partitions != null)
        {
            await partitions.AddTenantAsync(tenantId, token);
        }

        _logger.LogInformation("Added conjoined tenant {TenantId} for {DbContextType}", tenantId, typeof(T).Name);
        return tenantId.ToLowerInvariant();
    }

    public async Task DisableTenantAsync(string tenantId)
    {
        tenantId = options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        await registry.SetTenantDisabledAsync(tenantId, true);
        ConjoinedTenancy.SetTenantDisabled(typeof(T), tenantId, true);
    }

    public async Task EnableTenantAsync(string tenantId)
    {
        tenantId = options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        await registry.SetTenantDisabledAsync(tenantId, false);
        ConjoinedTenancy.SetTenantDisabled(typeof(T), tenantId, false);
    }

    public async Task RemoveTenantAsync(string tenantId)
    {
        tenantId = options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        await registry.DeleteTenantRecordAsync(tenantId);
        _active = _active.Remove(tenantId);
        ConjoinedTenancy.SetTenantDisabled(typeof(T), tenantId, false);

        if (partitions != null)
        {
            // Hard delete for a partitioned conjoined tenant is the partition drop
            await partitions.DropTenantAsync(tenantId, deleteData: true);
        }
    }

    public async Task<IReadOnlyList<string>> AllDisabledAsync()
    {
        var disabled = await registry.LoadDisabledTenantIdsAsync();
        ConjoinedTenancy.SetDisabledTenants(typeof(T), disabled);
        return disabled;
    }
}
