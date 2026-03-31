using ImTools;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Weasel.Core;

namespace Wolverine.RDBMS.MultiTenancy;

public interface ITenantDatabaseRegistry
{
    Task<string> TryFindTenantConnectionString(string tenantId);
    Task<IReadOnlyList<Assignment<string>>> LoadAllTenantConnectionStrings();

    IDatabaseProvider Provider { get; }

    /// <summary>
    /// Add or update a tenant record in the master tenants table.
    /// </summary>
    Task AddTenantRecordAsync(string tenantId, string connectionString);

    /// <summary>
    /// Set the disabled flag on a tenant record.
    /// </summary>
    Task SetTenantDisabledAsync(string tenantId, bool disabled);

    /// <summary>
    /// Delete a tenant record entirely from the master tenants table.
    /// </summary>
    Task DeleteTenantRecordAsync(string tenantId);

    /// <summary>
    /// Load all tenant IDs that are currently disabled.
    /// </summary>
    Task<IReadOnlyList<string>> LoadDisabledTenantIdsAsync();
}

public class MasterTenantSource : IDynamicTenantSource<string>
{
    private readonly ITenantDatabaseRegistry _tenantRegistry;
    private readonly WolverineOptions _options;
    
    // Seed databases should probably go on 
    private ImHashMap<string, string> _values = ImHashMap<string, string>.Empty;

    // Maybe just push in options?
    // Get TenantIdStyle on WolverineOptions.Durability
    public MasterTenantSource(ITenantDatabaseRegistry tenantRegistry, WolverineOptions options)
    {
        _tenantRegistry = tenantRegistry;
        _options = options;
    }

    public DatabaseCardinality Cardinality => DatabaseCardinality.DynamicMultiple;
    public async ValueTask<string> FindAsync(string tenantId)
    {
        tenantId = _options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        if (_values.TryFind(tenantId, out var connectionString)) return connectionString;

        connectionString =  await _tenantRegistry.TryFindTenantConnectionString(tenantId);
        connectionString = _tenantRegistry.Provider.AddApplicationNameToConnectionString(connectionString, _options.ServiceName);
        
        if (connectionString.IsEmpty())
        {
            throw new UnknownTenantIdException(tenantId);
        }

        _values = _values.AddOrUpdate(tenantId, connectionString);

        return connectionString;
    }

    public async Task RefreshAsync()
    {
        var allAssignments = await _tenantRegistry.LoadAllTenantConnectionStrings();

        foreach (var assignment in allAssignments)
        {
            var tenantId = _options.Durability.TenantIdStyle.MaybeCorrectTenantId(assignment.TenantId);
            if (!_values.Contains(tenantId))
            {
                var connectionString =
                    _tenantRegistry.Provider.AddApplicationNameToConnectionString(assignment.Value, _options.ServiceName);

                _values = _values.AddOrUpdate(tenantId, connectionString);
            }
        }
    }

    public IReadOnlyList<string> AllActive()
    {
        return _values.Enumerate().Select(x => x.Value).Distinct().ToList();
    }

    public IReadOnlyList<Assignment<string>> AllActiveByTenant()
    {
        return _values.Enumerate().Select(pair => new Assignment<string>(pair.Key, pair.Value)).ToList();
    }

    public async Task AddTenantAsync(string tenantId, string connectionValue)
    {
        tenantId = _options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        await _tenantRegistry.AddTenantRecordAsync(tenantId, connectionValue);

        var connectionString = _tenantRegistry.Provider.AddApplicationNameToConnectionString(connectionValue, _options.ServiceName);
        _values = _values.AddOrUpdate(tenantId, connectionString);
    }

    public async Task DisableTenantAsync(string tenantId)
    {
        tenantId = _options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        await _tenantRegistry.SetTenantDisabledAsync(tenantId, true);
        _values = _values.Remove(tenantId);
    }

    public async Task RemoveTenantAsync(string tenantId)
    {
        tenantId = _options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        await _tenantRegistry.DeleteTenantRecordAsync(tenantId);
        _values = _values.Remove(tenantId);
    }

    public async Task<IReadOnlyList<string>> AllDisabledAsync()
    {
        return await _tenantRegistry.LoadDisabledTenantIdsAsync();
    }

    public async Task EnableTenantAsync(string tenantId)
    {
        tenantId = _options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        await _tenantRegistry.SetTenantDisabledAsync(tenantId, false);

        // Re-populate the cache for this tenant
        var connectionString = await _tenantRegistry.TryFindTenantConnectionString(tenantId);
        if (connectionString.IsNotEmpty())
        {
            connectionString = _tenantRegistry.Provider.AddApplicationNameToConnectionString(connectionString, _options.ServiceName);
            _values = _values.AddOrUpdate(tenantId, connectionString);
        }
    }
}