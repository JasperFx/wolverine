using System.Reflection;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
internal sealed class EFCoreSagaStoreDiagnostics : ISagaStoreDiagnostics
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

    public Task<IReadOnlyList<SagaTypeDescriptor>> GetRegisteredSagaTypesAsync(CancellationToken ct)
    {
        var distinct = sagaIndex().Values
            .GroupBy(v => v.sagaType)
            .Select(g => g.First().sagaType)
            .ToArray();

        var descriptors = distinct.Select(buildDescriptor).ToArray();
        return Task.FromResult<IReadOnlyList<SagaTypeDescriptor>>(descriptors);
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
        if (!sagaIndex().TryGetValue(sagaTypeName, out var pair))
            return Array.Empty<SagaInstanceState>();

        var clamped = count <= 0 ? 0 : Math.Min(count, 1000);
        if (clamped == 0) return Array.Empty<SagaInstanceState>();

        using var scope = _services.CreateScope();
        var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(pair.dbContextType);

        var helper = typeof(EFCoreSagaStoreDiagnostics)
            .GetMethod(nameof(querySagasAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(pair.sagaType);

        var task = (Task<IReadOnlyList<SagaInstanceState>>)helper
            .Invoke(this, [dbContext, pair.sagaType, clamped, ct])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SagaInstanceState>> querySagasAsync<TSaga>(
        DbContext dbContext, Type sagaType, int count, CancellationToken ct) where TSaga : class
    {
        var sagas = await dbContext.Set<TSaga>().AsNoTracking().Take(count).ToListAsync(ct).ConfigureAwait(false);
        var list = new List<SagaInstanceState>(sagas.Count);
        foreach (var saga in sagas)
        {
            var id = extractIdentity(dbContext, saga, sagaType) ?? Guid.Empty;
            list.Add(buildInstance(sagaType, id, saga));
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
            "EFCore");
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
            var sagaTypes = _runtime.Options.HandlerGraph.Chains
                .OfType<global::Wolverine.Persistence.Sagas.SagaChain>()
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
