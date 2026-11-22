using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx;
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
internal sealed partial class DocumentStoreSagaStoreDiagnostics : ISagaStoreDiagnostics, ISagaIdleDiagnostics, ISagaRetentionSweeper
{
    private readonly IWolverineRuntime _runtime;
    private readonly IDocumentSessionFactory _sessions;
    private readonly Type _providerType;
    private readonly string _storeName;
    private readonly Type? _ancillaryStoreType;
    private readonly object _gate = new();
    private Dictionary<string, (Type SagaType, Type IdType)>? _sagaIndex;

    /// <param name="runtime">The Wolverine runtime, for the handler graph and persistence providers.</param>
    /// <param name="sessions">The store's sessions, as the store-agnostic JasperFx factory.</param>
    /// <param name="providerType">
    ///     The store's <see cref="IPersistenceFrameProvider" /> implementation type, used to decide which saga
    ///     types this store owns.
    /// </param>
    /// <param name="storeName">The name reported on each <see cref="SagaDescriptor" />, e.g. "Marten".</param>
    /// <param name="ancillaryStoreType">
    ///     GH-5799. The marker type of the ancillary store these sessions come from, such as <c>IOtherStore</c>
    ///     in <c>[MartenStore(typeof(IOtherStore))]</c>, or null for the main store. A saga type belongs to the
    ///     store its handler chains are routed to, so the main store no longer claims an ancillary store's
    ///     sagas and then reads them from the wrong database.
    /// </param>
    public DocumentStoreSagaStoreDiagnostics(IWolverineRuntime runtime, IDocumentSessionFactory sessions,
        Type providerType, string storeName, Type? ancillaryStoreType = null)
    {
        _runtime = runtime;
        _sessions = sessions;
        _providerType = providerType;
        _storeName = storeName;
        _ancillaryStoreType = ancillaryStoreType;
    }

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
    {
        var descriptors = sagaIndex().Values.Select(x => x.SagaType).Distinct()
            .Select(x =>
            {
                var descriptor = SagaDescriptorBuilder.Build(_runtime.Options.HandlerGraph, x, _storeName);

                // GH-5415: an event-sourced saga's history is its stream
                if (x.CanBeCastTo<EventSourcedSaga>())
                {
                    descriptor.SupportsHistory = true;
                    descriptor.HistoryKind = SagaHistoryKind.EventStream;
                }
                return descriptor;
            })
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

        // GH-5415: an event-sourced saga the application keeps Live has no snapshot to load, so aggregate its stream
        if (saga is null && entry.SagaType.CanBeCastTo<EventSourcedSaga>())
        {
            saga = await aggregateAsync(entry.SagaType, identity, 0, null, ct).ConfigureAwait(false);
        }

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
        => sagaIndex().TryGetValue(sagaTypeName, out var owned) && !isLiveEventSourced(owned.SagaType)
            ? DocumentQueryCriteria.IsAvailable
                ? SagaQueryCapabilities.Full
                : SagaQueryCapabilities.PagingOnly(
                    "Saga filters and orderings need runtime code generation, which is unavailable in a Native AOT process.")
            : SagaQueryCapabilities.None;

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Runs in a native image. Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code, so no new native code is needed. Same suppression the Marten-only copy carried (GH-5142).")]
    public Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options,
        CancellationToken ct)
    {
        return QuerySagasAsync(sagaTypeName, options, false, ct);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Runs in a native image. Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code, so no new native code is needed. Same suppression the Marten-only copy carried (GH-5142).")]
    public async Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options,
        bool includeCompleted, CancellationToken ct)
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
            [session, entry.SagaType, options, includeCompleted, pageNumber, pageSize, ct])!;
        return await task.ConfigureAwait(false);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    private static async Task<SagaQueryResult> querySagasAsync<TSaga>(IDocumentReadOperations session,
        Type sagaType, DocumentQueryOptions options, bool includeCompleted, int pageNumber, int pageSize,
        CancellationToken ct)
        where TSaga : notnull
    {
        var source = SagaCompletionQueries.OpenOnly(session.Query<TSaga>(), includeCompleted);
        var queryable = SagaQueryCriteria.Apply(options, source, "Id");
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

    // GH-5558: Saga.LastModified is a member of every saga document (Marten also maps it onto mt_last_modified), so
    // the idle queries are plain LINQ over the saga type

    private const string TenantRefusal =
        "Idle saga queries on a document store read the default tenant's sagas only.";

    public bool SupportsIdleQueries(string sagaTypeName)
        => sagaIndex().TryGetValue(sagaTypeName, out var entry) && entry.SagaType.CanBeCastTo<Saga>();

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code.")]
    public async Task<long> CountIdleSagasAsync(string sagaTypeName, DateTimeOffset modifiedBefore, string? tenantId,
        CancellationToken ct)
    {
        SagaQueryCriteria.RefuseTenant(tenantId, TenantRefusal);
        if (!SupportsIdleQueries(sagaTypeName)) return 0;
        var entry = sagaIndex()[sagaTypeName];

        await using var session = _sessions.QuerySession();
        var task = (Task<long>)typeof(DocumentStoreSagaStoreDiagnostics)
            .GetMethod(nameof(countIdleAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(entry.SagaType)
            .Invoke(null, [session, modifiedBefore, ct])!;
        return await task.ConfigureAwait(false);
    }

    private static async Task<long> countIdleAsync<TSaga>(IDocumentReadOperations session, DateTimeOffset modifiedBefore,
        CancellationToken ct) where TSaga : Saga
    {
        return await session.Query<TSaga>().Where(x => x.CompletedAt == null && x.LastModified < modifiedBefore)
            .CountAsync(ct).ConfigureAwait(false);
    }

    // GH-5572: retained, completed sagas are swept with a plain DeleteWhere

    public string RetentionStoreName => _storeName;

    public IReadOnlyList<Type> SagaTypesForRetention() => sagaIndex().Values.Select(x => x.SagaType).Distinct().ToArray();

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code.")]
    public async Task<long> DeleteCompletedSagasAsync(Type sagaType, DateTimeOffset completedBefore, CancellationToken ct)
    {
        if (!sagaType.CanBeCastTo<Saga>() || !sagaIndex().ContainsKey(sagaType.FullName!)) return 0;

        // GH-5415: an event-sourced saga's stream is archived, never deleted, and its snapshot removed
        if (sagaType.CanBeCastTo<EventSourcedSaga>())
        {
            return await archiveCompletedAsync(sagaType, completedBefore, ct).ConfigureAwait(false);
        }

        var task = (Task<long>)typeof(DocumentStoreSagaStoreDiagnostics)
            .GetMethod(nameof(deleteCompletedAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType)
            .Invoke(null, [_sessions, completedBefore, ct])!;
        return await task.ConfigureAwait(false);
    }

    private static async Task<long> deleteCompletedAsync<TSaga>(IDocumentSessionFactory sessions,
        DateTimeOffset completedBefore, CancellationToken ct) where TSaga : Saga
    {
        await using var session = sessions.LightweightSession();
        var count = await SagaCompletionQueries.CompletedBefore(session.Query<TSaga>(), completedBefore)
            .CountAsync(ct).ConfigureAwait(false);
        if (count == 0) return 0;

        session.DeleteWhere<TSaga>(x => x.CompletedAt != null && x.CompletedAt < completedBefore);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return count;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code.")]
    public async Task<SagaQueryResult> QuerySagasByLastModifiedAsync(string sagaTypeName, SagaLastModifiedRange range,
        DocumentQueryOptions options, CancellationToken ct)
    {
        var (tenantId, rest) = SagaQueryCriteria.TakeTenant(options);
        SagaQueryCriteria.RefuseTenant(tenantId, TenantRefusal);

        var (pageNumber, pageSize) = SagaQueryCriteria.Page(rest);
        SagaQueryCriteria.RefuseCriteria(rest, GetQueryCapabilities(sagaTypeName));
        if (!SupportsIdleQueries(sagaTypeName)) return SagaQueryResult.Empty(pageNumber, pageSize);
        var entry = sagaIndex()[sagaTypeName];

        await using var session = _sessions.QuerySession();
        var task = (Task<SagaQueryResult>)typeof(DocumentStoreSagaStoreDiagnostics)
            .GetMethod(nameof(queryByLastModifiedAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(entry.SagaType)
            .Invoke(null, [session, entry.SagaType, range, rest, pageNumber, pageSize, ct])!;
        return await task.ConfigureAwait(false);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    private static async Task<SagaQueryResult> queryByLastModifiedAsync<TSaga>(IDocumentReadOperations session,
        Type sagaType, SagaLastModifiedRange range, DocumentQueryOptions options, int pageNumber, int pageSize,
        CancellationToken ct) where TSaga : Saga
    {
        // GH-5572: a retained, completed saga is not idle
        var queryable = SagaLastModifiedQueries.Filter(SagaCompletionQueries.OpenOnly(session.Query<TSaga>()), range);
        queryable = SagaLastModifiedQueries.Order(queryable, options, "Id");

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

            var sagaChains = _runtime.Options.HandlerGraph.SagaChains() // GH-5133: includes Separated saga chains
                .GroupBy(c => c.SagaType);

            foreach (var group in sagaChains)
            {
                var sagaType = group.Key;
                IPersistenceFrameProvider? owner;
                Type idType;
                try
                {
                    // The same precedence the saga chain's codegen uses: selective providers first, then the
                    // catch-all document stores in registration order.
                    if (!rules.TryFindPersistenceFrameProvider(container, sagaType, out owner)) continue;
                    if (owner.GetType() != _providerType) continue;

                    // GH-5799: and the same store the saga chain's codegen writes to
                    var chain = group.FirstOrDefault(c => c.DetermineAncillaryStoreType() != null) ?? group.First();
                    if (!ownsStore(chain.DetermineAncillaryStoreType(), container)) continue;

                    idType = owner.DetermineSagaIdType(sagaType, chain, container);
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

    /// <summary>
    ///     GH-5799. Whether a saga chain routed to <paramref name="chainStoreType" /> (its
    ///     <c>DetermineAncillaryStoreType()</c>) commits to this store. An ancillary store owns exactly the chains
    ///     routed to it. The main store owns the rest, including a chain routed to a store type the container
    ///     cannot resolve to a document store, because the saga codegen falls back to the main store for those too.
    /// </summary>
    private bool ownsStore(Type? chainStoreType, IServiceContainer container)
    {
        if (_ancillaryStoreType != null) return chainStoreType == _ancillaryStoreType;
        if (chainStoreType == null) return true;

        return container.Services.GetService(chainStoreType) is not IDocumentSessionFactory;
    }
}
