using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime;

/// <summary>
/// Aggregates several source-generated <see cref="IWolverineTypeLoader"/> instances
/// into a single loader that exposes the union of their discovered types. The
/// Wolverine source generator emits a <c>[WolverineTypeManifest]</c> attribute on
/// every handler-bearing assembly; this composite is what the runtime hands to
/// <see cref="HandlerGraph"/> when more than one such assembly is loaded so that
/// handlers in referenced assemblies are not silently dropped (#2632).
/// </summary>
internal sealed class CompositeWolverineTypeLoader : IWolverineTypeLoader
{
    private readonly IReadOnlyList<IWolverineTypeLoader> _inner;

    public CompositeWolverineTypeLoader(IReadOnlyList<IWolverineTypeLoader> inner)
    {
        if (inner == null) throw new ArgumentNullException(nameof(inner));
        if (inner.Count == 0)
            throw new ArgumentException("At least one inner loader is required", nameof(inner));

        _inner = inner;

        DiscoveredHandlerTypes = _inner.SelectMany(l => l.DiscoveredHandlerTypes).Distinct().ToList();

        DiscoveredMessageTypes = _inner.SelectMany(l => l.DiscoveredMessageTypes)
            .GroupBy(t => t.MessageType)
            .Select(g => g.First())
            .ToList();

        DiscoveredHttpEndpointTypes = _inner.SelectMany(l => l.DiscoveredHttpEndpointTypes).Distinct().ToList();

        DiscoveredExtensionTypes = _inner.SelectMany(l => l.DiscoveredExtensionTypes).Distinct().ToList();

        HasPreGeneratedHandlers = _inner.Any(l => l.HasPreGeneratedHandlers);

        if (HasPreGeneratedHandlers)
        {
            var merged = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var loader in _inner)
            {
                if (loader.PreGeneratedHandlerTypes is null) continue;
                foreach (var kvp in loader.PreGeneratedHandlerTypes)
                {
                    // First loader wins on collision — matches single-loader semantics for
                    // duplicate type names within one manifest. Logged at registration time
                    // by HandlerGraph; not re-logged here to avoid double noise.
                    merged.TryAdd(kvp.Key, kvp.Value);
                }
            }

            PreGeneratedHandlerTypes = merged;
        }
        else
        {
            PreGeneratedHandlerTypes = null;
        }
    }

    public IReadOnlyList<Type> DiscoveredHandlerTypes { get; }

    public IReadOnlyList<(Type MessageType, string Alias)> DiscoveredMessageTypes { get; }

    public IReadOnlyList<Type> DiscoveredHttpEndpointTypes { get; }

    public IReadOnlyList<Type> DiscoveredExtensionTypes { get; }

    public bool HasPreGeneratedHandlers { get; }

    public IReadOnlyDictionary<string, Type>? PreGeneratedHandlerTypes { get; }

    public Type? TryFindPreGeneratedType(string typeName)
    {
        foreach (var loader in _inner)
        {
            var hit = loader.TryFindPreGeneratedType(typeName);
            if (hit is not null) return hit;
        }

        return null;
    }
}
