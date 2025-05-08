using ImTools;
using JasperFx.Core;
using JasperFx.Core.Descriptors;
using JasperFx.MultiTenancy;
using Weasel.Core;

namespace Wolverine.RDBMS.MultiTenancy;

public interface ITenantDatabaseRegistry
{
    Task<string> TryFindTenantConnectionString(string tenantId);
    Task<IReadOnlyList<Assignment<string>>> LoadAllTenantConnectionStrings();
    
    IDatabaseProvider Provider { get; }
}

public class MasterTenantSource : ITenantedSource<string>
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
}