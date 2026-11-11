using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Documents;
using JasperFx.Events.Documents;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     GH-5142. The <see cref="ISagaStoreDiagnostics" /> for a document store integration — Marten, Polecat or
///     Fisher — written once over the store-agnostic JasperFx document contracts
///     (<see cref="IDocumentSessionFactory" />, <see cref="IDocumentReadOperations" /> and
///     <see cref="DocumentQueryableExtensions" />) rather than once per store.
/// </summary>
/// <remarks>
///     <para>
///     This was Marten's alone. Polecat and Fisher registered the lightweight-RDBMS
///     <c>DatabaseSagaStoreDiagnostics</c> instead, which reads Wolverine's own saga tables. But a saga in a
///     Polecat or Fisher application is a Polecat or Fisher <b>document</b>, so those tables are never written,
///     and the saga explorer showed nothing for either store. That was the Polecat/Fisher gap.
///     </para>
///     <para>
///     A saga type belongs to this storage when the store's persistence provider is the one Wolverine would
///     choose for it — the first in consultation order, with selective providers such as EF Core ahead of the
///     catch-all document stores — so a host that also runs EF Core sagas does not see them listed twice.
///     </para>
/// </remarks>
internal sealed class DocumentStoreSagaStoreDiagnostics : ISagaStoreDiagnostics
{
    private readonly IWolverineRuntime _runtime;
    private readonly IDocumentSessionFactory _sessions;
    private readonly Type _providerType;
    private readonly string _storeName;
    private readonly object _gate = new();
    private Dictionary<string, (Type SagaType, Type IdType)>? _sagaIndex;

    /// <param name="runtime">The Wolverine runtime, for the handler graph and persistence providers.</param>
    /// <param name="sessions">The store's sessions, as the store-agnostic JasperFx factory.</param>
    /// <param name="providerType">
    ///     The store's <see cref="IPersistenceFrameProvider" /> implementation type, used to decide which saga
    ///     types this store owns.
    /// </param>
    /// <param name="storeName">The name reported on each <see cref="SagaDescriptor" />, e.g. "Marten".</param>
    public DocumentStoreSagaStoreDiagnostics(IWolverineRuntime runtime, IDocumentSessionFactory sessions,
        Type providerType, string storeName)
    {
        _runtime = runtime;
        _sessions = sessions;
        _providerType = providerType;
        _storeName = storeName;
    }

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
    {
        var descriptors = sagaIndex().Values.Select(x => x.SagaType).Distinct()
            .Select(x => SagaDescriptorBuilder.Build(_runtime.Options.HandlerGraph, x, _storeName))
            .ToArray();

        return Task.FromResult<IReadOnlyList<SagaDescriptor>>(descriptors);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Runs in a native image. Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code, so no new native code is needed. Same suppression the Marten-only copy carried (GH-5142).")]
    public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var entry)) return null;

        var coercedId = coerceIdentity(identity, entry.IdType);
        if (coercedId is null) return null;

        await using var session = _sessions.QuerySession();

        // The saga type is only known at runtime, so close the generic load over it. Every store overrides
        // IDocumentReadOperations.LoadAsync<T>(object) to resolve its own identity types — int, long and
        // strong-typed ids included — so this is not limited to the Guid/string default.
        var task = (Task<object?>)typeof(DocumentStoreSagaStoreDiagnostics)
            .GetMethod(nameof(loadAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(entry.SagaType)
            .Invoke(null, [session, coercedId, ct])!;

        var saga = await task.ConfigureAwait(false);
        return saga is null ? null : buildInstance(entry.SagaType, identity, saga);
    }

    private static async Task<object?> loadAsync<TSaga>(IDocumentReadOperations session, object id,
        CancellationToken ct) where TSaga : notnull
    {
        return await session.LoadAsync<TSaga>(id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count,
        CancellationToken ct)
    {
        if (!sagaIndex().ContainsKey(sagaTypeName) || count <= 0) return Array.Empty<SagaInstanceState>();

        // Ordered by saga identity (the QuerySagasAsync default) so the sample is repeatable.
        var result = await QuerySagasAsync(sagaTypeName, new DocumentQueryOptions(1, count), ct).ConfigureAwait(false);
        return result.Instances;
    }

    public SagaQueryCapabilities GetQueryCapabilities(string sagaTypeName)
        => sagaIndex().ContainsKey(sagaTypeName)
            ? DocumentQueryCriteria.IsAvailable
                ? SagaQueryCapabilities.Full
                : SagaQueryCapabilities.PagingOnly(
                    "Saga filters and orderings need runtime code generation, which is unavailable in a Native AOT process.")
            : SagaQueryCapabilities.None;

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Runs in a native image. Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code, so no new native code is needed. Same suppression the Marten-only copy carried (GH-5142).")]
    public async Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options,
        CancellationToken ct)
    {
        var (pageNumber, pageSize) = SagaQueryCriteria.Page(options);
        SagaQueryCriteria.RefuseCriteria(options, GetQueryCapabilities(sagaTypeName));
        if (!sagaIndex().TryGetValue(sagaTypeName, out var entry))
        {
            return SagaQueryResult.Empty(pageNumber, pageSize);
        }

        await using var session = _sessions.QuerySession();

        var helper = typeof(DocumentStoreSagaStoreDiagnostics)
            .GetMethod(nameof(querySagasAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(entry.SagaType);

        var task = (Task<SagaQueryResult>)helper.Invoke(null,
            [session, entry.SagaType, options, pageNumber, pageSize, ct])!;
        return await task.ConfigureAwait(false);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    private static async Task<SagaQueryResult> querySagasAsync<TSaga>(IDocumentReadOperations session,
        Type sagaType, DocumentQueryOptions options, int pageNumber, int pageSize, CancellationToken ct)
        where TSaga : notnull
    {
        var queryable = SagaQueryCriteria.Apply(options, session.Query<TSaga>(), "Id");
        try
        {
            var total = await queryable.CountAsync(ct).ConfigureAwait(false);
            var sagas = await queryable.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(ct)
                .ConfigureAwait(false);

            var list = new List<SagaInstanceState>(sagas.Count);
            foreach (var saga in sagas)
            {
                var id = extractIdentity(saga, sagaType) ?? Guid.Empty;
                list.Add(buildInstance(sagaType, id, saga));
            }

            return new SagaQueryResult(list, total, pageNumber, pageSize);
        }
        catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e))
        {
            throw options.Untranslatable(e);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Diagnostics only: serializes saga state for a monitoring tool. Saga state types are rooted by handler discovery. Same suppression the Marten-only copy carried (GH-5142).")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Diagnostics only: serializes saga state for a monitoring tool. Without reflection-based JSON in a native image this read fails, not message handling. Same suppression the Marten-only copy carried (GH-5142).")]
    private static SagaInstanceState buildInstance(Type sagaType, object identity, object saga)
    {
        var stateJson = JsonSerializer.SerializeToElement(saga, sagaType);
        var isCompleted = saga is Saga sagaBase && sagaBase.IsCompleted();

        // LastModified would cost a metadata round trip per saga in a list call, which is too much for a
        // diagnostic peek, so it is left null.
        return new SagaInstanceState(sagaType.FullNameInCode(), identity, isCompleted, stateJson, null);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Reads the conventional Id member of a saga state type rooted by handler discovery, whose Id the saga codegen already binds to.")]
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
        if (identity.GetType() == targetIdType) return identity;

        // The interface boxes the id as object, so a caller may hand over a string for a Guid id (a URL
        // parameter rehydrated from JSON). Try the obvious conversions and give up quietly otherwise: null
        // reads upstream as "saga not found", which is right for a mistyped id.
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

    private Dictionary<string, (Type SagaType, Type IdType)> sagaIndex()
    {
        if (_sagaIndex is not null) return _sagaIndex;
        lock (_gate)
        {
            if (_sagaIndex is not null) return _sagaIndex;

            var index = new Dictionary<string, (Type, Type)>(StringComparer.Ordinal);
            var rules = _runtime.Options.CodeGeneration;
            var container = _runtime.Options.HandlerGraph.Container;

            var sagaTypes = _runtime.Options.HandlerGraph.SagaChains() // GH-5133: includes Separated saga chains
                .Select(c => c.SagaType)
                .Distinct();

            foreach (var sagaType in sagaTypes)
            {
                IPersistenceFrameProvider? owner;
                Type idType;
                try
                {
                    // The same precedence the saga chain's codegen uses: selective providers first, then the
                    // catch-all document stores in registration order.
                    if (!rules.TryFindPersistenceFrameProvider(container, sagaType, out owner)) continue;
                    if (owner.GetType() != _providerType) continue;

                    idType = owner.DetermineSagaIdType(sagaType, container);
                }
                catch
                {
                    // CanPersist may probe DI for a session or context. A resolution failure means this
                    // provider is not applicable, not that the whole diagnostic surface should fail.
                    continue;
                }

                // Indexed by FullName (canonical) and Name (caller-friendly short form) so the aggregator can
                // route on either.
                index.TryAdd(sagaType.FullName!, (sagaType, idType));
                index.TryAdd(sagaType.Name, (sagaType, idType));
            }

            _sagaIndex = index;
            return index;
        }
    }
}
