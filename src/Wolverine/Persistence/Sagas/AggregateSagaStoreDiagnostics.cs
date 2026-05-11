using Wolverine.Configuration.Capabilities;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Internal fan-out implementation of <see cref="ISagaStoreDiagnostics"/>
/// used when a Wolverine host registers more than one saga storage —
/// e.g. Marten for one set of saga types and EF Core for another.
/// Routes <see cref="ReadSagaAsync"/> and
/// <see cref="ListSagaInstancesAsync"/> to whichever child storage owns
/// the requested saga type, and concatenates
/// <see cref="GetRegisteredSagasAsync"/> across all children so the
/// caller sees one unified saga catalog. When no child owns the
/// requested type, read returns <c>null</c> and list returns empty —
/// the same contract a single child uses for unknown types.
/// </summary>
/// <remarks>
/// The (saga-type-name → child) routing table is built lazily on first
/// access by walking each child's
/// <see cref="ISagaStoreDiagnostics.GetRegisteredSagasAsync"/>, then
/// cached for the lifetime of this aggregator. If two children claim
/// the same saga-type name (a misconfiguration), the first registered
/// wins — Wolverine's saga handler routing is single-storage-per-type
/// at runtime, so any duplication here points at a duplicate
/// <c>IPersistenceFrameProvider</c> registration upstream.
/// </remarks>
internal sealed class AggregateSagaStoreDiagnostics : ISagaStoreDiagnostics
{
    private readonly IReadOnlyList<ISagaStoreDiagnostics> _children;
    private readonly SemaphoreSlim _routeLock = new(1, 1);
    private Dictionary<string, ISagaStoreDiagnostics>? _routes;

    public AggregateSagaStoreDiagnostics(IEnumerable<ISagaStoreDiagnostics> children)
    {
        _children = children.Where(c => c is not AggregateSagaStoreDiagnostics).ToArray();
    }

    public async Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
    {
        if (_children.Count == 0) return Array.Empty<SagaDescriptor>();

        var all = new List<SagaDescriptor>();
        foreach (var child in _children)
        {
            var descriptors = await child.GetRegisteredSagasAsync(ct).ConfigureAwait(false);
            all.AddRange(descriptors);
        }

        return all;
    }

    public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        var routes = await ensureRoutesAsync(ct).ConfigureAwait(false);
        return routes.TryGetValue(sagaTypeName, out var child)
            ? await child.ReadSagaAsync(sagaTypeName, identity, ct).ConfigureAwait(false)
            : null;
    }

    public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct)
    {
        var routes = await ensureRoutesAsync(ct).ConfigureAwait(false);
        return routes.TryGetValue(sagaTypeName, out var child)
            ? await child.ListSagaInstancesAsync(sagaTypeName, count, ct).ConfigureAwait(false)
            : Array.Empty<SagaInstanceState>();
    }

    private async Task<Dictionary<string, ISagaStoreDiagnostics>> ensureRoutesAsync(CancellationToken ct)
    {
        if (_routes is not null) return _routes;

        await _routeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_routes is not null) return _routes;

            // Index by FullName (canonical) AND Name (caller-friendly short form)
            // so CritterWatch can route on whichever the user typed.
            var map = new Dictionary<string, ISagaStoreDiagnostics>(StringComparer.Ordinal);
            foreach (var child in _children)
            {
                var descriptors = await child.GetRegisteredSagasAsync(ct).ConfigureAwait(false);
                foreach (var descriptor in descriptors)
                {
                    map.TryAdd(descriptor.StateType.FullName, child);
                    map.TryAdd(descriptor.StateType.Name, child);
                }
            }

            _routes = map;
            return _routes;
        }
        finally
        {
            _routeLock.Release();
        }
    }
}
