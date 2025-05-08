using ImTools;
using JasperFx.Core.Descriptors;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.RDBMS;

public abstract class TenantedMessageDatabaseSource<TStore, TInput> : ITenantedMessageSource, IDatabaseSource where TStore : IMessageStore, IDatabase
{
    private readonly ITenantedSource<TInput> _source;
    private ImHashMap<TInput, TStore> _tenants = ImHashMap<TInput, TStore>.Empty;

    protected TenantedMessageDatabaseSource(ITenantedSource<TInput> source, IWolverineRuntime runtime)
    {
        _source = source;
    }

    public DatabaseCardinality Cardinality => _source.Cardinality;
    public ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        throw new NotImplementedException();
    }

    public async Task RefreshAsync()
    {
        await _source.RefreshAsync();
        var activeInputs = _source.AllActive();
        foreach (var input in activeInputs)
        {
            if (!_tenants.TryFind(input, out var store))
            {
                // Watch that a store isn't doubled up
                
            }
        }
    }

    public IReadOnlyList<IMessageStore> AllActive()
    {
        throw new NotImplementedException();
    }

    public ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
    {
        throw new NotImplementedException();
    }
}