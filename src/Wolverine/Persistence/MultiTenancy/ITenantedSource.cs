using ImTools;
using JasperFx;
using JasperFx.Core.Descriptors;

namespace Wolverine.Persistence.MultiTenancy;

public interface ITenantedSource<T>
{
    DatabaseCardinality Cardinality { get; }
    ValueTask<T> FindAsync(string tenantId);
    Task RefreshAsync();
    IReadOnlyList<T> AllActive();
}

public class StaticTenantSource<T> : ITenantedSource<T>
{
    private ImHashMap<string, T> _values = ImHashMap<string, T>.Empty;

    public void RegisterDefault(T value) => Register(StorageConstants.DefaultTenantId, value);
    
    public void Register(string tenantId, T connectionString)
    {
        _values = _values.AddOrUpdate(tenantId, connectionString);
    }

    public DatabaseCardinality Cardinality => DatabaseCardinality.StaticMultiple;
    public ValueTask<T> FindAsync(string tenantId)
    {
        if (_values.TryFind(tenantId, out var connectionString)) return new ValueTask<T>(connectionString);

        throw new ArgumentOutOfRangeException(nameof(tenantId), "Unknown tenant id");
    }

    public Task RefreshAsync()
    {
        return Task.CompletedTask;
    }

    public IReadOnlyList<T> AllActive()
    {
        return _values.Enumerate().Select(x => x.Value).Distinct().ToList();
    }
}

public class StaticConnectionStringSource : StaticTenantSource<string>
{

}