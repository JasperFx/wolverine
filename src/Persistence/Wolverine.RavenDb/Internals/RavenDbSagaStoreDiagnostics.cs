using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.RavenDb.Internals;

/// <summary>
/// RavenDB-backed implementation of <see cref="ISagaStoreDiagnostics"/>.
/// Walks the Wolverine handler graph for every saga state type the
/// <see cref="RavenDbPersistenceFrameProvider"/> can persist, then
/// routes <c>ReadSaga</c> and <c>ListSagaInstances</c> calls through
/// the registered <see cref="IDocumentStore"/> so monitoring tools can
/// surface saga state JSON without reaching into Raven directly.
/// </summary>
/// <remarks>
/// Wolverine's runtime aggregator fans out across every registered
/// <see cref="ISagaStoreDiagnostics"/>, so this provider participates
/// alongside Marten / EF Core / any other saga storage when a host
/// wires more than one. RavenDB sagas use string identifiers
/// regardless of the saga's id member type — see
/// <see cref="RavenDbPersistenceFrameProvider.DetermineSagaIdType"/>.
/// </remarks>
internal sealed class RavenDbSagaStoreDiagnostics : ISagaStoreDiagnostics
{
    private readonly IWolverineRuntime _runtime;
    private readonly IDocumentStore _store;
    private readonly object _gate = new();
    private Dictionary<string, Type>? _sagaIndex;

    public RavenDbSagaStoreDiagnostics(IWolverineRuntime runtime, IDocumentStore store)
    {
        _runtime = runtime;
        _store = store;
    }

    public Task<IReadOnlyList<SagaTypeDescriptor>> GetRegisteredSagaTypesAsync(CancellationToken ct)
    {
        var distinct = sagaIndex().Values.Distinct().ToArray();
        var descriptors = distinct.Select(buildDescriptor).ToArray();
        return Task.FromResult<IReadOnlyList<SagaTypeDescriptor>>(descriptors);
    }

    // session.LoadAsync<TSaga>(string id, ct) is invoked reflectively because
    // sagaType is only known at runtime. The Task<TSaga> return value is
    // unwrapped via Result-property reflection. AOT-clean apps that need
    // saga diagnostics on RavenDb preserve their saga state types via
    // TrimmerRootDescriptor (or use the Wolverine codegen-static path which
    // bakes the typed LoadAsync invocation into source-generated code).
    [UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "Generic IAsyncDocumentSession.LoadAsync<TSaga> invoked reflectively; saga types statically rooted via handler discovery. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Generic IAsyncDocumentSession.LoadAsync<TSaga> invoked reflectively; saga types statically rooted via handler discovery. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "task.GetType().GetProperty(\"Result\") on a Task<TSaga>; the closed Task<TSaga> type has Result by construction. See AOT guide.")]
    public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType)) return null;

        using var session = _store.OpenAsyncSession();
        var id = identity?.ToString();
        if (id is null) return null;

        // session.LoadAsync<TSaga>(string id, ct) — generic; reflection
        // because the saga type is only known at runtime.
        var loadAsync = typeof(IAsyncDocumentSession)
            .GetMethods()
            .FirstOrDefault(m => m.Name == nameof(IAsyncDocumentSession.LoadAsync)
                                 && m.IsGenericMethodDefinition
                                 && m.GetParameters().Length == 2
                                 && m.GetParameters()[0].ParameterType == typeof(string)
                                 && m.GetParameters()[1].ParameterType == typeof(CancellationToken));
        if (loadAsync is null) return null;

        var task = (Task)loadAsync.MakeGenericMethod(sagaType).Invoke(session, [id, ct])!;
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

        using var session = _store.OpenAsyncSession();
        var helper = typeof(RavenDbSagaStoreDiagnostics)
            .GetMethod(nameof(querySagasAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType);

        var task = (Task<IReadOnlyList<SagaInstanceState>>)helper.Invoke(this, [session, sagaType, clamped, ct])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SagaInstanceState>> querySagasAsync<TSaga>(
        IAsyncDocumentSession session, Type sagaType, int count, CancellationToken ct) where TSaga : class
    {
        var sagas = await session.Query<TSaga>().Take(count).ToListAsync(ct).ConfigureAwait(false);
        var list = new List<SagaInstanceState>(sagas.Count);
        foreach (var saga in sagas)
        {
            var id = session.Advanced.GetDocumentId(saga) ?? extractIdentity(saga, sagaType);
            list.Add(buildInstance(sagaType, id ?? string.Empty, saga));
        }
        return list;
    }

    private SagaTypeDescriptor buildDescriptor(Type sagaType)
    {
        var (starting, continuing) = SagaMessageBuckets.For(sagaType, _runtime.Options.HandlerGraph);
        return new SagaTypeDescriptor(
            TypeDescriptor.For(sagaType),
            starting,
            continuing,
            "RavenDb");
    }

    // JsonSerializer.SerializeToElement(saga, sagaType) over a runtime-resolved
    // saga type. AOT-clean apps that need saga diagnostics on RavenDb supply
    // a JsonSerializerContext covering their saga types (or preserve via
    // TrimmerRootDescriptor). Same chunk D (default JSON serializer) pattern.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reflection-based STJ over runtime saga type; AOT consumers supply a JsonSerializerContext for their saga types. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Reflection-based STJ over runtime saga type; AOT consumers supply a JsonSerializerContext for their saga types. See AOT guide.")]
    private static SagaInstanceState buildInstance(Type sagaType, object identity, object saga)
    {
        var stateJson = JsonSerializer.SerializeToElement(saga, sagaType);
        var isCompleted = saga is global::Wolverine.Saga sagaBase && sagaBase.IsCompleted();
        return new SagaInstanceState(
            sagaType.FullNameInCode(),
            identity,
            isCompleted,
            stateJson,
            null);
    }

    // GetProperty("Id") / GetField("Id") on a runtime-resolved saga type when
    // RavenDB's session.Advanced.GetDocumentId fails. Same chunk Q
    // (InMemorySagaPersistor) / chunk P (SagaChain.DetermineSagaIdMember)
    // pattern: saga types are statically rooted via handler discovery.
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetProperty/GetField(\"Id\") on runtime saga type; saga types statically rooted via handler discovery. See AOT guide.")]
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

    private Dictionary<string, Type> sagaIndex()
    {
        if (_sagaIndex is not null) return _sagaIndex;
        lock (_gate)
        {
            if (_sagaIndex is not null) return _sagaIndex;

            var index = new Dictionary<string, Type>(StringComparer.Ordinal);
            var providers = _runtime.Options.CodeGeneration.PersistenceProviders();
            var raven = providers.OfType<RavenDbPersistenceFrameProvider>().FirstOrDefault();
            if (raven is null)
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
                    canPersist = raven.CanPersist(sagaType, container, out _);
                }
                catch
                {
                    canPersist = false;
                }

                if (!canPersist) continue;
                index.TryAdd(sagaType.FullName!, sagaType);
                index.TryAdd(sagaType.Name, sagaType);
            }

            _sagaIndex = index;
            return index;
        }
    }
}
