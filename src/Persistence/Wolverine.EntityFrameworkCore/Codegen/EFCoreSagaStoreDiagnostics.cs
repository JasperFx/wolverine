using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// EF Core implementation of <see cref="ISagaStoreDiagnostics"/>. Walks
/// the Wolverine handler graph for saga state types EF Core can persist
/// — i.e. types <see cref="EFCorePersistenceFrameProvider"/> can map to
/// a registered <see cref="DbContext"/> — then routes <c>ReadSaga</c>
/// and <c>ListSagaInstances</c> calls through that DbContext to surface
/// the underlying entity state for monitoring tools.
/// </summary>
/// <remarks>
/// Wolverine wraps every registered <see cref="ISagaStoreDiagnostics"/>
/// in an aggregator before exposing it on
/// <see cref="IWolverineRuntime.SagaStorage"/>, so callers see one
/// unified saga catalog regardless of how many DbContexts (or other
/// saga storages) the host wires up. Hosts that register multiple
/// DbContexts get correct routing too — each saga is dispatched to
/// whichever DbContext owns its entity model.
/// </remarks>
// AOT note (#2746): saga diagnostics over runtime saga / DbContext types.
// Same chunk Z (RavenDb) pattern: AOT consumers preserve saga state types
// via TrimmerRootDescriptor or supply a JsonSerializerContext for the saga
// state. Class-level suppression matches the EFCorePersistenceFrameProvider
// approach above.
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "EFCore saga diagnostics — reflection-based STJ over saga state; AOT consumers supply a JsonSerializerContext. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2060",
    Justification = "EFCore saga diagnostics — generic DbContext.Set<TSaga> invoked reflectively; saga types statically rooted via handler discovery. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2067",
    Justification = "EFCore saga diagnostics — saga / DbContext types statically rooted via persistence registration. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2075",
    Justification = "EFCore saga diagnostics — Id member walk on runtime saga type; statically rooted via handler discovery. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2091",
    Justification = "EFCore saga diagnostics — generic helper closes over saga state type at runtime; bounded by handler discovery. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2111",
    Justification = "EFCore saga diagnostics — reflective method invoke on DbContext member; bounded by saga registration. See AOT guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "EFCore saga diagnostics — MakeGenericMethod over runtime saga / DbContext types. See AOT guide.")]
internal sealed class EFCoreSagaStoreDiagnostics : ISagaStoreDiagnostics, ISagaIdleDiagnostics, ISagaRetentionSweeper
{
    private readonly IWolverineRuntime _runtime;
    private readonly IServiceProvider _services;
    private readonly object _gate = new();
    private Dictionary<string, (Type sagaType, Type dbContextType)>? _sagaIndex;

    public EFCoreSagaStoreDiagnostics(IWolverineRuntime runtime, IServiceProvider services)
    {
        _runtime = runtime;
        _services = services;
    }

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
    {
        var distinct = sagaIndex().Values
            .GroupBy(v => v.sagaType)
            .Select(g => g.First().sagaType)
            .ToArray();

        var descriptors = distinct.Select(buildDescriptor).ToArray();
        return Task.FromResult<IReadOnlyList<SagaDescriptor>>(descriptors);
    }

    public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var pair)) return null;

        using var scope = _services.CreateScope();
        var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(pair.dbContextType);

        // dbContext.Set<TSaga>() and Set<TSaga>().FindAsync(id, ct) are
        // both generic; reflection dispatch is the simplest way to
        // bridge from the Type-erased ISagaStoreDiagnostics surface.
        var setMethod = typeof(DbContext)
            .GetMethods()
            .First(m => m.Name == nameof(DbContext.Set)
                        && m.IsGenericMethodDefinition
                        && m.GetParameters().Length == 0);
        var dbSet = setMethod.MakeGenericMethod(pair.sagaType).Invoke(dbContext, null);
        if (dbSet is null) return null;

        var coercedId = coerceIdentity(identity, expectedIdType(dbContext, pair.sagaType));
        if (coercedId is null) return null;

        // DbSet<T>.FindAsync(object?[] keyValues, CancellationToken ct)
        // returns ValueTask<T?>. Reflection dispatch + AsTask() because
        // the saga type is only known at runtime here.
        var findAsync = dbSet.GetType()
            .GetMethods()
            .FirstOrDefault(m => m.Name == "FindAsync"
                                 && m.GetParameters().Length == 2
                                 && m.GetParameters()[0].ParameterType == typeof(object?[])
                                 && m.GetParameters()[1].ParameterType == typeof(CancellationToken));
        if (findAsync is null) return null;

        var valueTask = findAsync.Invoke(dbSet, new object?[] { new object?[] { coercedId }, ct });
        if (valueTask is null) return null;

        var asTaskMethod = valueTask.GetType().GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
        if (asTaskMethod?.Invoke(valueTask, null) is not Task asTask) return null;
        await asTask.ConfigureAwait(false);
        var saga = asTask.GetType().GetProperty("Result")!.GetValue(asTask);
        if (saga is null) return null;

        return buildInstance(pair.sagaType, identity, saga);
    }

    public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct)
    {
        if (!sagaIndex().ContainsKey(sagaTypeName) || count <= 0)
            return Array.Empty<SagaInstanceState>();

        // Ordered by the primary key (the QuerySagasAsync default) so the sample is repeatable.
        var result = await QuerySagasAsync(sagaTypeName, new DocumentQueryOptions(1, count), ct).ConfigureAwait(false);
        return result.Instances;
    }

    public SagaQueryCapabilities GetQueryCapabilities(string sagaTypeName)
        => sagaIndex().ContainsKey(sagaTypeName)
            ? DocumentQueryCriteria.IsAvailable
                ? SagaQueryCapabilities.Full
                : SagaQueryCapabilities.PagingOnly("Saga filters and orderings need runtime code generation, which is unavailable in a Native AOT process.")
            : SagaQueryCapabilities.None;

    public Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options, CancellationToken ct)
    {
        return QuerySagasAsync(sagaTypeName, options, false, ct);
    }

    public async Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options,
        bool includeCompleted, CancellationToken ct)
    {
        var (pageNumber, pageSize) = SagaQueryCriteria.Page(options);
        SagaQueryCriteria.RefuseCriteria(options, GetQueryCapabilities(sagaTypeName));
        if (!sagaIndex().TryGetValue(sagaTypeName, out var pair))
            return SagaQueryResult.Empty(pageNumber, pageSize);

        using var scope = _services.CreateScope();
        var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(pair.dbContextType);

        var helper = typeof(EFCoreSagaStoreDiagnostics)
            .GetMethod(nameof(querySagasAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(pair.sagaType);

        var task = (Task<SagaQueryResult>)helper
            .Invoke(this, [dbContext, pair.sagaType, options, includeCompleted, pageNumber, pageSize, ct])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<SagaQueryResult> querySagasAsync<TSaga>(
        DbContext dbContext, Type sagaType, DocumentQueryOptions options, bool includeCompleted, int pageNumber,
        int pageSize, CancellationToken ct) where TSaga : class
    {
        var keyName = dbContext.Model.FindEntityType(sagaType)?.FindPrimaryKey()?.Properties[0].Name ?? "Id";
        var source = openOnly(dbContext, sagaType, dbContext.Set<TSaga>().AsNoTracking(), includeCompleted);
        var queryable = SagaQueryCriteria.Apply(options, source, keyName);
        try
        {
            var total = await queryable.CountAsync(ct).ConfigureAwait(false);
            var sagas = await queryable.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(ct)
                .ConfigureAwait(false);
            var list = new List<SagaInstanceState>(sagas.Count);
            foreach (var saga in sagas)
            {
                var id = extractIdentity(dbContext, saga, sagaType) ?? Guid.Empty;
                list.Add(buildInstance(sagaType, id, saga));
            }

            return new SagaQueryResult(list, total, pageNumber, pageSize);
        }
        catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e))
        {
            throw options.Untranslatable(e);
        }
    }

    // GH-5839: complete one saga from outside its handlers, through the DbContext that owns the saga's entity

    public bool SupportsSagaCompletion(string sagaTypeName)
        => sagaIndex().TryGetValue(sagaTypeName, out var pair) && pair.sagaType.CanBeCastTo<global::Wolverine.Saga>();

    public async Task<SagaTerminationResult> CompleteSagaAsync(string sagaTypeName, object identity, string reason,
        CancellationToken ct)
    {
        if (!SupportsSagaCompletion(sagaTypeName))
        {
            throw SagaTermination.NotSupported(sagaTypeName, "EF Core saga storage");
        }

        var pair = sagaIndex()[sagaTypeName];

        using var scope = _services.CreateScope();
        var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(pair.dbContextType);

        var id = coerceIdentity(identity, expectedIdType(dbContext, pair.sagaType));
        if (id is null)
        {
            return new SagaTerminationResult(pair.sagaType.FullNameInCode(), identity, SagaTerminationOutcome.NotFound);
        }

        var task = (Task<SagaTerminationOutcome>)typeof(EFCoreSagaStoreDiagnostics)
            .GetMethod(nameof(completeAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(pair.sagaType)
            .Invoke(null, [dbContext, id, SagaTermination.IsRetained(pair.sagaType, _runtime.Options), ct])!;

        var outcome = await task.ConfigureAwait(false);
        return new SagaTerminationResult(pair.sagaType.FullNameInCode(), identity, outcome);
    }

    private static async Task<SagaTerminationOutcome> completeAsync<TSaga>(DbContext dbContext, object id, bool retained,
        CancellationToken ct) where TSaga : global::Wolverine.Saga
    {
        var saga = await dbContext.Set<TSaga>().FindAsync([id], ct).ConfigureAwait(false);
        if (saga is null) return SagaTerminationOutcome.NotFound;
        if (saga.IsCompleted()) return SagaTerminationOutcome.AlreadyCompleted;

        if (retained)
        {
            SagaTermination.MarkRetainedCompleted(saga);
        }
        else
        {
            dbContext.Remove(saga);
        }

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException e)
        {
            // Only where the model maps a concurrency token for the saga, as for the saga's own handlers
            throw SagaTermination.Concurrency(typeof(TSaga), id, e);
        }

        return retained ? SagaTerminationOutcome.Retained : SagaTerminationOutcome.Deleted;
    }

    // GH-5558: idle saga diagnostics over the mapped Saga.LastModified property

    private const string TenantRefusal =
        "Idle saga queries on EF Core read the DbContext the container resolves, not a tenant's.";

    public bool SupportsIdleQueries(string sagaTypeName)
        => sagaIndex().TryGetValue(sagaTypeName, out var pair) && pair.sagaType.CanBeCastTo<global::Wolverine.Saga>();

    public async Task<long> CountIdleSagasAsync(string sagaTypeName, DateTimeOffset modifiedBefore, string? tenantId,
        CancellationToken ct)
    {
        SagaQueryCriteria.RefuseTenant(tenantId, TenantRefusal);
        if (!SupportsIdleQueries(sagaTypeName)) return 0;
        var pair = sagaIndex()[sagaTypeName];

        using var scope = _services.CreateScope();
        var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(pair.dbContextType);
        assertLastModifiedIsMapped(dbContext, pair.sagaType);

        var task = (Task<long>)typeof(EFCoreSagaStoreDiagnostics)
            .GetMethod(nameof(countIdleAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(pair.sagaType)
            .Invoke(null, [dbContext, modifiedBefore, ct])!;
        return await task.ConfigureAwait(false);
    }

    private static Task<long> countIdleAsync<TSaga>(DbContext dbContext, DateTimeOffset modifiedBefore,
        CancellationToken ct) where TSaga : global::Wolverine.Saga
    {
        return openOnly(dbContext, typeof(TSaga), dbContext.Set<TSaga>().AsNoTracking())
            .Where(x => x.LastModified < modifiedBefore).LongCountAsync(ct);
    }

    /// <summary>
    /// GH-5572. Leave out retained, completed sagas. A model that does not map <see cref="global::Wolverine.Saga.CompletedAt" />
    /// cannot hold any, since retention is refused for it, so the query is left alone.
    /// </summary>
    private static IQueryable<TSaga> openOnly<TSaga>(DbContext dbContext, Type sagaType, IQueryable<TSaga> source,
        bool includeCompleted = false)
    {
        if (includeCompleted || !completedAtIsMapped(dbContext, sagaType)) return source;
        return SagaCompletionQueries.OpenOnly(source);
    }

    private static bool completedAtIsMapped(DbContext dbContext, Type sagaType)
        => dbContext.Model.FindEntityType(sagaType)?.FindProperty(nameof(global::Wolverine.Saga.CompletedAt)) is not null;

    // GH-5572: retained, completed sagas are swept with ExecuteDeleteAsync

    public string RetentionStoreName => "EntityFrameworkCore";

    public IReadOnlyList<Type> SagaTypesForRetention()
        => sagaIndex().Values.Select(x => x.sagaType).Where(x => x.CanBeCastTo<global::Wolverine.Saga>()).Distinct().ToArray();

    public async Task<long> DeleteCompletedSagasAsync(Type sagaType, DateTimeOffset completedBefore, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaType.FullName!, out var pair) || !sagaType.CanBeCastTo<global::Wolverine.Saga>()) return 0;

        using var scope = _services.CreateScope();
        var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(pair.dbContextType);
        if (!completedAtIsMapped(dbContext, sagaType)) return 0;

        var task = (Task<int>)typeof(EFCoreSagaStoreDiagnostics)
            .GetMethod(nameof(deleteCompletedAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType)
            .Invoke(null, [dbContext, completedBefore, ct])!;
        return await task.ConfigureAwait(false);
    }

    private static Task<int> deleteCompletedAsync<TSaga>(DbContext dbContext, DateTimeOffset completedBefore,
        CancellationToken ct) where TSaga : global::Wolverine.Saga
    {
        return SagaCompletionQueries.CompletedBefore(dbContext.Set<TSaga>(), completedBefore).ExecuteDeleteAsync(ct);
    }

    public async Task<SagaQueryResult> QuerySagasByLastModifiedAsync(string sagaTypeName, SagaLastModifiedRange range,
        DocumentQueryOptions options, CancellationToken ct)
    {
        var (tenantId, rest) = SagaQueryCriteria.TakeTenant(options);
        SagaQueryCriteria.RefuseTenant(tenantId, TenantRefusal);

        var (pageNumber, pageSize) = SagaQueryCriteria.Page(rest);
        SagaQueryCriteria.RefuseCriteria(rest, GetQueryCapabilities(sagaTypeName));
        if (!SupportsIdleQueries(sagaTypeName)) return SagaQueryResult.Empty(pageNumber, pageSize);
        var pair = sagaIndex()[sagaTypeName];

        using var scope = _services.CreateScope();
        var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(pair.dbContextType);
        assertLastModifiedIsMapped(dbContext, pair.sagaType);

        var task = (Task<SagaQueryResult>)typeof(EFCoreSagaStoreDiagnostics)
            .GetMethod(nameof(queryByLastModifiedAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(pair.sagaType)
            .Invoke(null, [dbContext, pair.sagaType, range, rest, pageNumber, pageSize, ct])!;
        return await task.ConfigureAwait(false);
    }

    private static async Task<SagaQueryResult> queryByLastModifiedAsync<TSaga>(DbContext dbContext, Type sagaType,
        SagaLastModifiedRange range, DocumentQueryOptions options, int pageNumber, int pageSize, CancellationToken ct)
        where TSaga : global::Wolverine.Saga
    {
        var keyName = dbContext.Model.FindEntityType(sagaType)?.FindPrimaryKey()?.Properties[0].Name ?? "Id";
        // GH-5572: a retained, completed saga is not idle
        var queryable = SagaLastModifiedQueries.Filter(openOnly(dbContext, sagaType, dbContext.Set<TSaga>().AsNoTracking()), range);
        queryable = SagaLastModifiedQueries.Order(queryable, options, keyName);

        try
        {
            var total = await queryable.LongCountAsync(ct).ConfigureAwait(false);
            var sagas = await queryable.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(ct)
                .ConfigureAwait(false);
            var list = new List<SagaInstanceState>(sagas.Count);
            foreach (var saga in sagas)
            {
                var id = extractIdentity(dbContext, saga, sagaType) ?? Guid.Empty;
                list.Add(buildInstance(sagaType, id, saga));
            }

            return new SagaQueryResult(list, total, pageNumber, pageSize);
        }
        catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e))
        {
            throw options.Untranslatable(e);
        }
    }

    /// <summary>
    /// The 7.0 migration guide lets an application Ignore() Saga.LastModified instead of adding the column. Such a
    /// model cannot answer an idle query, so refuse it by name rather than surface EF's translation error.
    /// </summary>
    private static void assertLastModifiedIsMapped(DbContext dbContext, Type sagaType)
    {
        if (dbContext.Model.FindEntityType(sagaType)?.FindProperty(nameof(global::Wolverine.Saga.LastModified)) is null)
        {
            throw new DocumentCriteriaNotSupportedException(nameof(global::Wolverine.Saga.LastModified),
                $"Saga.LastModified is not mapped for {sagaType.FullNameInCode()} in {dbContext.GetType().FullNameInCode()}. Map it (and add a migration) to query idle sagas");
        }
    }

    private SagaDescriptor buildDescriptor(Type sagaType)
    {
        return SagaDescriptorBuilder.Build(_runtime.Options.HandlerGraph, sagaType, "EntityFrameworkCore");
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
            null);
    }

    private static Type expectedIdType(DbContext dbContext, Type sagaType)
    {
        var entityType = dbContext.Model.FindEntityType(sagaType);
        var pk = entityType?.FindPrimaryKey();
        var keyType = pk?.GetKeyType();
        return keyType ?? typeof(Guid);
    }

    private static object? extractIdentity(DbContext dbContext, object saga, Type sagaType)
    {
        var entry = dbContext.Entry(saga);
        var pk = entry.Metadata.FindPrimaryKey();
        if (pk is null || pk.Properties.Count == 0) return null;
        return entry.Property(pk.Properties[0].Name).CurrentValue;
    }

    private static object? coerceIdentity(object identity, Type targetIdType)
    {
        if (identity is null) return null;
        var sourceType = identity.GetType();
        if (sourceType == targetIdType) return identity;

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

    private Dictionary<string, (Type sagaType, Type dbContextType)> sagaIndex()
    {
        if (_sagaIndex is not null) return _sagaIndex;
        lock (_gate)
        {
            if (_sagaIndex is not null) return _sagaIndex;

            var index = new Dictionary<string, (Type, Type)>(StringComparer.Ordinal);
            var providers = _runtime.Options.CodeGeneration.PersistenceProviders();
            var efcore = providers.OfType<EFCorePersistenceFrameProvider>().FirstOrDefault();
            if (efcore is null)
            {
                _sagaIndex = index;
                return index;
            }

            var container = _runtime.Options.HandlerGraph.Container;
            var sagaTypes = _runtime.Options.HandlerGraph.SagaChains() // GH-5133: includes Separated saga chains
                .Select(c => c.SagaType)
                .Distinct();

            foreach (var sagaType in sagaTypes)
            {
                Type? dbContextType;
                try
                {
                    dbContextType = efcore.TryDetermineDbContextType(sagaType, container);
                }
                catch
                {
                    dbContextType = null;
                }

                if (dbContextType is null) continue;

                var pair = (sagaType, dbContextType);
                index.TryAdd(sagaType.FullName!, pair);
                index.TryAdd(sagaType.Name, pair);
            }

            _sagaIndex = index;
            return index;
        }
    }
}
