using System.Reflection;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Marten;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Marten.Persistence.Sagas;

/// <summary>
/// Marten-backed implementation of <see cref="ISagaStoreDiagnostics"/>.
/// Walks the Wolverine handler graph for saga state types Marten can
/// persist (i.e. types <see cref="MartenPersistenceFrameProvider"/>
/// claims), then routes <c>ReadSaga</c> / <c>ListSagaInstances</c>
/// calls through Marten's <see cref="IDocumentStore"/> to surface the
/// underlying state JSON for monitoring tools.
/// </summary>
/// <remarks>
/// Wolverine wraps every registered <see cref="ISagaStoreDiagnostics"/>
/// in an aggregator before exposing it on
/// <see cref="IWolverineRuntime.SagaStorage"/>, so callers see one
/// unified saga catalog even when a host wires Marten alongside EF Core
/// or another saga storage. This implementation is registered by
/// <c>WolverineOptionsMartenExtensions.IntegrateWithWolverine</c>.
/// </remarks>
internal sealed class MartenSagaStoreDiagnostics : ISagaStoreDiagnostics
{
    private readonly IWolverineRuntime _runtime;
    private readonly IDocumentStore _store;
    private readonly object _gate = new();
    private Dictionary<string, Type>? _sagaIndex;

    public MartenSagaStoreDiagnostics(IWolverineRuntime runtime, IDocumentStore store)
    {
        _runtime = runtime;
        _store = store;
    }

    public Task<IReadOnlyList<SagaTypeDescriptor>> GetRegisteredSagaTypesAsync(CancellationToken ct)
    {
        var distinctTypes = sagaIndex().Values.Distinct().ToArray();
        var descriptors = distinctTypes.Select(buildDescriptor).ToArray();
        return Task.FromResult<IReadOnlyList<SagaTypeDescriptor>>(descriptors);
    }

    public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType)) return null;

        await using var session = _store.LightweightSession();
        var idType = _store.Options.FindOrResolveDocumentType(sagaType).IdType;
        var coercedId = coerceIdentity(identity, idType);
        if (coercedId is null) return null;

        // session.LoadAsync<TSaga>(id, ct) — dispatched via reflection
        // because the saga type isn't known at compile time and Marten
        // overloads LoadAsync per supported id type (Guid/int/long/string).
        var loadAsync = typeof(IDocumentSession)
            .GetMethods()
            .FirstOrDefault(m => m.Name == nameof(IDocumentSession.LoadAsync)
                                 && m.IsGenericMethodDefinition
                                 && m.GetParameters().Length == 2
                                 && m.GetParameters()[0].ParameterType == coercedId.GetType()
                                 && m.GetParameters()[1].ParameterType == typeof(CancellationToken));
        if (loadAsync is null) return null;

        var task = (Task)loadAsync.MakeGenericMethod(sagaType).Invoke(session, [coercedId, ct])!;
        await task.ConfigureAwait(false);
        var saga = task.GetType().GetProperty("Result")!.GetValue(task);
        if (saga is null) return null;

        return buildInstance(sagaType, identity, saga);
    }

    public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType))
            return Array.Empty<SagaInstanceState>();

        var clamped = count <= 0 ? 0 : Math.Min(count, 1000);
        if (clamped == 0) return Array.Empty<SagaInstanceState>();

        await using var session = _store.QuerySession();

        var helper = typeof(MartenSagaStoreDiagnostics)
            .GetMethod(nameof(querySagasAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType);

        var task = (Task<IReadOnlyList<SagaInstanceState>>)helper.Invoke(this, [session, sagaType, clamped, ct])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SagaInstanceState>> querySagasAsync<TSaga>(
        IQuerySession session, Type sagaType, int count, CancellationToken ct) where TSaga : class
    {
        var sagas = await session.Query<TSaga>().Take(count).ToListAsync(ct).ConfigureAwait(false);
        var list = new List<SagaInstanceState>(sagas.Count);
        foreach (var saga in sagas)
        {
            var id = extractIdentity(saga, sagaType) ?? Guid.Empty;
            list.Add(buildInstance(sagaType, id, saga));
        }
        return list;
    }

    private SagaTypeDescriptor buildDescriptor(Type sagaType)
    {
        // Re-walk the handler graph for this saga's chains so the
        // Starting/Continuing message split matches what
        // ServiceCapabilities.SagaTypes computes. Same source of truth,
        // no risk of drift between the per-storage view and the
        // host-wide capabilities snapshot.
        var (starting, continuing) = SagaMessageBuckets.For(sagaType, _runtime.Options.HandlerGraph);
        return new SagaTypeDescriptor(
            TypeDescriptor.For(sagaType),
            starting,
            continuing,
            "Marten");
    }

    private static SagaInstanceState buildInstance(Type sagaType, object identity, object saga)
    {
        var stateJson = JsonSerializer.SerializeToElement(saga, sagaType);
        var isCompleted = saga is global::Wolverine.Saga sagaBase && sagaBase.IsCompleted();
        return new SagaInstanceState(
            sagaType.FullNameInCode(),
            identity,
            isCompleted,
            stateJson,
            // Marten exposes LastModified through MetadataForAsync, but
            // doing one extra round-trip per saga in a list-call is too
            // expensive for a diagnostic peek. Leave it null and let a
            // future iteration query metadata in batch when needed.
            null);
    }

    private static object? extractIdentity(object saga, Type sagaType)
    {
        var idMember = (MemberInfo?)sagaType.GetProperty("Id") ?? sagaType.GetField("Id");
        return idMember switch
        {
            PropertyInfo p => p.GetValue(saga),
            FieldInfo f => f.GetValue(saga),
            _ => null
        };
    }

    private static object? coerceIdentity(object identity, Type targetIdType)
    {
        if (identity is null) return null;
        var sourceType = identity.GetType();
        if (sourceType == targetIdType) return identity;

        // The interface boxes the id as `object`, so the caller might
        // hand us a string for a Guid id (URL parameter rehydrated
        // from JSON). Try the obvious string→primitive conversions and
        // give up quietly otherwise — returning null routes upstream
        // as "saga not found", the right behaviour for a mistyped id.
        try
        {
            if (targetIdType == typeof(Guid) && identity is string s) return Guid.Parse(s);
            if (targetIdType == typeof(int) && identity is string si) return int.Parse(si);
            if (targetIdType == typeof(long) && identity is string sl) return long.Parse(sl);
            return Convert.ChangeType(identity, targetIdType);
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, Type> sagaIndex()
    {
        if (_sagaIndex is not null) return _sagaIndex;
        lock (_gate)
        {
            if (_sagaIndex is not null) return _sagaIndex;

            var index = new Dictionary<string, Type>(StringComparer.Ordinal);
            var providers = _runtime.Options.CodeGeneration.PersistenceProviders();
            var marten = providers.OfType<MartenPersistenceFrameProvider>().FirstOrDefault();
            if (marten is null)
            {
                _sagaIndex = index;
                return index;
            }

            var container = _runtime.Options.HandlerGraph.Container;
            var sagaTypes = _runtime.Options.HandlerGraph.Chains
                .OfType<SagaChain>()
                .Select(c => c.SagaType)
                .Distinct();

            foreach (var sagaType in sagaTypes)
            {
                bool canPersist;
                try
                {
                    canPersist = marten.CanPersist(sagaType, container, out _);
                }
                catch
                {
                    canPersist = false;
                }

                if (!canPersist) continue;

                // Index by FullName (canonical) and Name (caller-friendly
                // short form) so the aggregator can route on either.
                index.TryAdd(sagaType.FullName!, sagaType);
                index.TryAdd(sagaType.Name, sagaType);
            }

            _sagaIndex = index;
            return index;
        }
    }
}
