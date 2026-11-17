using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Documents;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.CosmosDb.Internals;

/// <summary>
///     CosmosDB-backed implementation of <see cref="ISagaStoreDiagnostics" />. Every saga document lives in the
///     one <see cref="Container" /> the message store uses, so reads are point reads by id (in the saga's own
///     partition when <see cref="CosmosDbConfiguration.PartitionSagasById" /> is on, otherwise the undefined
///     partition) and queries go through <c>Container.GetItemLinqQueryable</c> with the shared
///     <see cref="SagaQueryCriteria" /> path.
/// </summary>
/// <remarks>
///     The container has no per-type discriminator on saga documents (the same limitation that
///     <see cref="QueryableFrame" /> documents), so a listing or query for one saga type is scoped to the
///     container, not to that type. A container holding more than one saga type, or Wolverine's own envelope
///     documents, will deserialize those as the saga type being queried. A query's own <c>Where</c> is the way to
///     narrow it.
/// </remarks>
internal sealed class CosmosDbSagaStoreDiagnostics : ISagaStoreDiagnostics, ISagaIdleDiagnostics, ISagaRetentionSweeper
{
    private readonly Container _container;
    private readonly IWolverineRuntime _runtime;
    private readonly object _gate = new();
    private Dictionary<string, Type>? _sagaIndex;

    public CosmosDbSagaStoreDiagnostics(IWolverineRuntime runtime, Container container)
    {
        _runtime = runtime;
        _container = container;
    }

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
    {
        var descriptors = sagaIndex().Values.Distinct()
            .Select(t => SagaDescriptorBuilder.Build(_runtime.Options.HandlerGraph, t, "CosmosDb"))
            .ToArray();
        return Task.FromResult<IReadOnlyList<SagaDescriptor>>(descriptors);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "Generic Container.ReadItemAsync<TSaga> invoked reflectively; saga types statically rooted via handler discovery. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Generic Container.ReadItemAsync<TSaga> invoked reflectively; saga types statically rooted via handler discovery. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "task.GetType().GetProperty(\"Result\") on a Task<ItemResponse<TSaga>>; the closed type has Result by construction.")]
    public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType)) return null;

        var id = identity?.ToString();
        if (id is null) return null;

        var partitionKey = partitionById(sagaType) ? new PartitionKey(id) : PartitionKey.None;

        try
        {
            var readItem = typeof(Container).GetMethod(nameof(Container.ReadItemAsync))!.MakeGenericMethod(sagaType);
            var task = (Task)readItem.Invoke(_container, [id, partitionKey, null, ct])!;
            await task.ConfigureAwait(false);

            var response = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var saga = response.GetType().GetProperty("Resource")!.GetValue(response);
            return saga is null ? null : buildInstance(sagaType, id, saga);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count,
        CancellationToken ct)
    {
        if (!sagaIndex().ContainsKey(sagaTypeName) || count <= 0) return Array.Empty<SagaInstanceState>();

        // Ordered by saga identity (the QuerySagasAsync default) so the sample is repeatable.
        var result = await QuerySagasAsync(sagaTypeName, new DocumentQueryOptions(1, count), ct)
            .ConfigureAwait(false);
        return result.Instances;
    }

    public SagaQueryCapabilities GetQueryCapabilities(string sagaTypeName)
        => sagaIndex().ContainsKey(sagaTypeName)
            ? DocumentQueryCriteria.IsAvailable
                ? SagaQueryCapabilities.Full
                : SagaQueryCapabilities.PagingOnly(
                    "Saga filters and orderings need runtime code generation, which is unavailable in a Native AOT process.")
            : SagaQueryCapabilities.None;

    public Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options,
        CancellationToken ct)
    {
        return QuerySagasAsync(sagaTypeName, options, false, ct);
    }

    public async Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options,
        bool includeCompleted, CancellationToken ct)
    {
        var (pageNumber, pageSize) = SagaQueryCriteria.Page(options);
        SagaQueryCriteria.RefuseCriteria(options, GetQueryCapabilities(sagaTypeName));
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType))
            return SagaQueryResult.Empty(pageNumber, pageSize);

        var helper = typeof(CosmosDbSagaStoreDiagnostics)
            .GetMethod(nameof(querySagasAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType);

        var task = (Task<SagaQueryResult>)helper.Invoke(this, [sagaType, options, includeCompleted, pageNumber, pageSize, ct])!;
        return await task.ConfigureAwait(false);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    private async Task<SagaQueryResult> querySagasAsync<TSaga>(Type sagaType, DocumentQueryOptions options,
        bool includeCompleted, int pageNumber, int pageSize, CancellationToken ct) where TSaga : class
    {
        var idMember = idMemberName(sagaType);
        IQueryable<TSaga> source = _container.GetItemLinqQueryable<TSaga>();
        if (!includeCompleted && typeof(TSaga).CanBeCastTo<Saga>())
        {
            source = openOnly(source);
        }

        try
        {
            var queryable = SagaQueryCriteria.Apply(options, source, idMember);

            var total = (await queryable.CountAsync(ct).ConfigureAwait(false)).Resource;

            var page = queryable.Skip((pageNumber - 1) * pageSize).Take(pageSize);
            var list = new List<SagaInstanceState>();
            using var iterator = page.ToFeedIterator();
            while (iterator.HasMoreResults)
            {
                foreach (var saga in await iterator.ReadNextAsync(ct).ConfigureAwait(false))
                {
                    list.Add(buildInstance(sagaType, extractIdentity(saga, sagaType) ?? string.Empty, saga));
                }
            }

            return new SagaQueryResult(list, total, pageNumber, pageSize);
        }
        catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e) || isCosmosTranslationFailure(e))
        {
            throw options.Untranslatable(e);
        }
    }

    // GH-5558: idle saga diagnostics over the LastModified member of the saga document. Saga documents share the
    // Wolverine container with each other and carry no type marker, so a query over one saga type cannot tell its
    // documents from another saga type's. Idle queries are therefore answered only when this is the application's
    // one Cosmos-stored saga type; every query also requires LastModified, which only saga documents have.

    private const string TenantRefusal =
        "Idle saga queries on CosmosDB read the registered container, not a tenant's.";

    public bool SupportsIdleQueries(string sagaTypeName)
        => sagaIndex().TryGetValue(sagaTypeName, out var sagaType) && sagaType.CanBeCastTo<Saga>()
                                                                   && sagaIndex().Values.Distinct().Count() == 1;

    private void assertIdleQueriesSupported(string sagaTypeName)
    {
        if (sagaIndex().Values.Distinct().Count() > 1)
        {
            throw new NotSupportedException(
                $"Idle saga queries on CosmosDB need {sagaTypeName} to be the only saga type stored in the Wolverine container: saga documents carry no type marker, so the count would include other saga types' documents.");
        }
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code.")]
    public async Task<long> CountIdleSagasAsync(string sagaTypeName, DateTimeOffset modifiedBefore, string? tenantId,
        CancellationToken ct)
    {
        SagaQueryCriteria.RefuseTenant(tenantId, TenantRefusal);
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType)) return 0;
        assertIdleQueriesSupported(sagaTypeName);

        var task = (Task<long>)typeof(CosmosDbSagaStoreDiagnostics)
            .GetMethod(nameof(countIdleAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType)
            .Invoke(this, [modifiedBefore.ToUniversalTime(), ct])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<long> countIdleAsync<TSaga>(DateTimeOffset modifiedBefore, CancellationToken ct)
        where TSaga : Saga
    {
        var queryable = openOnly(sagaDocuments<TSaga>()).Where(x => x.LastModified < modifiedBefore);
        return (await queryable.CountAsync(ct).ConfigureAwait(false)).Resource;
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
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType)) return SagaQueryResult.Empty(pageNumber, pageSize);
        assertIdleQueriesSupported(sagaTypeName);

        var utc = new SagaLastModifiedRange(range.ModifiedAfter?.ToUniversalTime(), range.ModifiedBefore?.ToUniversalTime());
        var task = (Task<SagaQueryResult>)typeof(CosmosDbSagaStoreDiagnostics)
            .GetMethod(nameof(queryByLastModifiedAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType)
            .Invoke(this, [sagaType, utc, rest, pageNumber, pageSize, ct])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<SagaQueryResult> queryByLastModifiedAsync<TSaga>(Type sagaType, SagaLastModifiedRange range,
        DocumentQueryOptions options, int pageNumber, int pageSize, CancellationToken ct) where TSaga : Saga
    {
        try
        {
            var queryable = SagaLastModifiedQueries.Order(
                SagaLastModifiedQueries.Filter(openOnly(sagaDocuments<TSaga>()), range), options, idMemberName(sagaType));

            var total = (await queryable.CountAsync(ct).ConfigureAwait(false)).Resource;

            var list = new List<SagaInstanceState>();
            using var iterator = queryable.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToFeedIterator();
            while (iterator.HasMoreResults)
            {
                foreach (var saga in await iterator.ReadNextAsync(ct).ConfigureAwait(false))
                {
                    list.Add(buildInstance(sagaType, extractIdentity(saga, sagaType) ?? string.Empty, saga));
                }
            }

            return new SagaQueryResult(list, total, pageNumber, pageSize);
        }
        catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e) || isCosmosTranslationFailure(e))
        {
            throw options.Untranslatable(e);
        }
    }

    /// <summary>
    /// GH-5572. Leave out retained, completed sagas. A document without CompletedAt at all (written before 7.0) has
    /// not completed: Cosmos does not treat an undefined property as null.
    /// </summary>
    private static IQueryable<TSaga> openOnly<TSaga>(IQueryable<TSaga> source)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(TSaga), "x");
        var completedAt = System.Linq.Expressions.Expression.Property(parameter,
            typeof(Saga).GetProperty(nameof(Saga.CompletedAt))!);
        var isDefined = System.Linq.Expressions.Expression.Call(
            typeof(CosmosLinqExtensions).GetMethod(nameof(CosmosLinqExtensions.IsDefined), [typeof(object)])!,
            System.Linq.Expressions.Expression.Convert(completedAt, typeof(object)));
        var isNull = System.Linq.Expressions.Expression.Equal(completedAt,
            System.Linq.Expressions.Expression.Constant(null, typeof(DateTimeOffset?)));
        var open = System.Linq.Expressions.Expression.OrElse(System.Linq.Expressions.Expression.Not(isDefined), isNull);
        return source.Where(System.Linq.Expressions.Expression.Lambda<Func<TSaga, bool>>(open, parameter));
    }

    // GH-5572: retained, completed sagas are found by query and deleted one by one. Saga documents carry no type
    // marker, so a sweep for one saga type sees every completed saga document in the container. Only retained saga
    // types ever keep a completed document, so with several saga types in the container the sweep uses the longest
    // retention period of them all: nothing is deleted early, though a saga with a shorter period may be kept longer.

    public string RetentionStoreName => "CosmosDb";

    public IReadOnlyList<Type> SagaTypesForRetention()
        => sagaIndex().Values.Where(x => x.CanBeCastTo<Saga>()).Distinct().ToArray();

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga state types are classes rooted by handler discovery, and a generic method closed over a reference type shares canonical code.")]
    public async Task<long> DeleteCompletedSagasAsync(Type sagaType, DateTimeOffset completedBefore, CancellationToken ct)
    {
        if (!sagaType.CanBeCastTo<Saga>() || !sagaIndex().ContainsKey(sagaType.FullName!)) return 0;

        var cutoff = completedBefore;
        var sagaTypes = SagaTypesForRetention();
        if (sagaTypes.Count > 1)
        {
            var longest = sagaTypes
                .Select(x => SagaRetention.For(x, _runtime.Options))
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Max();
            var conservative = DateTimeOffset.UtcNow.Subtract(longest);
            if (conservative < cutoff) cutoff = conservative;
        }

        var task = (Task<long>)typeof(CosmosDbSagaStoreDiagnostics)
            .GetMethod(nameof(deleteCompletedAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType)
            .Invoke(this, [sagaType, cutoff.ToUniversalTime(), ct])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<long> deleteCompletedAsync<TSaga>(Type sagaType, DateTimeOffset completedBefore,
        CancellationToken ct) where TSaga : Saga
    {
        var queryable = sagaDocuments<TSaga>()
            .Where(x => x.CompletedAt.IsDefined() && x.CompletedAt != null && x.CompletedAt < completedBefore);

        var ids = new List<string>();
        using (var iterator = queryable.ToFeedIterator())
        {
            while (iterator.HasMoreResults)
            {
                foreach (var saga in await iterator.ReadNextAsync(ct).ConfigureAwait(false))
                {
                    if (extractIdentity(saga, sagaType)?.ToString() is { } id) ids.Add(id);
                }
            }
        }

        long deleted = 0;
        foreach (var id in ids)
        {
            var partitionKey = partitionById(sagaType) ? new PartitionKey(id) : PartitionKey.None;
            try
            {
                await _container.DeleteItemAsync<TSaga>(id, partitionKey, cancellationToken: ct).ConfigureAwait(false);
                deleted++;
            }
            catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Already gone
            }
        }

        return deleted;
    }

    // Only saga documents have LastModified: Wolverine's own envelope, node and lock documents do not
    private IQueryable<TSaga> sagaDocuments<TSaga>() where TSaga : Saga
        => _container.GetItemLinqQueryable<TSaga>().Where(x => x.LastModified.IsDefined());

    // The Cosmos LINQ provider reports an expression it cannot translate as a NotSupportedException or
    // DocumentQueryException, and a server-side rejection of the generated SQL as a 400
    private static bool isCosmosTranslationFailure(Exception e)
        => e is CosmosException { StatusCode: System.Net.HttpStatusCode.BadRequest } or NotSupportedException
           || e.GetType().Name == "DocumentQueryException";

    private bool partitionById(Type sagaType)
        => _runtime.Services.GetService<CosmosDbConfiguration>() is { SagasArePartitionedById: true }
           && sagaType.CanBeCastTo<Saga>();

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetProperty/GetField(\"Id\") on runtime saga type; saga types statically rooted via handler discovery. See AOT guide.")]
    private static string idMemberName(Type sagaType)
    {
        var member = sagaType.GetProperty("Id") is not null || sagaType.GetField("Id") is not null
            ? "Id"
            : $"{sagaType.Name}Id";
        return member;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetProperty/GetField on runtime saga type; saga types statically rooted via handler discovery. See AOT guide.")]
    private static object? extractIdentity(object saga, Type sagaType)
    {
        var name = idMemberName(sagaType);
        var idMember = (MemberInfo?)sagaType.GetProperty(name) ?? sagaType.GetField(name);
        return idMember switch
        {
            PropertyInfo p => p.GetValue(saga),
            FieldInfo f => f.GetValue(saga),
            _ => null
        };
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reflection-based STJ over runtime saga type; AOT consumers supply a JsonSerializerContext for their saga types. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Reflection-based STJ over runtime saga type; AOT consumers supply a JsonSerializerContext for their saga types. See AOT guide.")]
    private static SagaInstanceState buildInstance(Type sagaType, object identity, object saga)
    {
        var stateJson = JsonSerializer.SerializeToElement(saga, sagaType);
        var isCompleted = saga is Saga sagaBase && sagaBase.IsCompleted();
        return new SagaInstanceState(sagaType.FullNameInCode(), identity, isCompleted, stateJson, null);
    }

    private Dictionary<string, Type> sagaIndex()
    {
        if (_sagaIndex is not null) return _sagaIndex;
        lock (_gate)
        {
            if (_sagaIndex is not null) return _sagaIndex;

            var index = new Dictionary<string, Type>(StringComparer.Ordinal);
            var cosmos = _runtime.Options.CodeGeneration.PersistenceProviders()
                .OfType<CosmosDbPersistenceFrameProvider>().FirstOrDefault();
            if (cosmos is null)
            {
                _sagaIndex = index;
                return index;
            }

            var container = _runtime.Options.HandlerGraph.Container;
            var sagaTypes = _runtime.Options.HandlerGraph.SagaChains().Select(c => c.SagaType).Distinct();

            foreach (var sagaType in sagaTypes)
            {
                bool canPersist;
                try
                {
                    canPersist = cosmos.CanPersist(sagaType, container, out _);
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
