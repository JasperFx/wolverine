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
internal sealed class CosmosDbSagaStoreDiagnostics : ISagaStoreDiagnostics
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

    public async Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options,
        CancellationToken ct)
    {
        var (pageNumber, pageSize) = SagaQueryCriteria.Page(options);
        SagaQueryCriteria.RefuseCriteria(options, GetQueryCapabilities(sagaTypeName));
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType))
            return SagaQueryResult.Empty(pageNumber, pageSize);

        var helper = typeof(CosmosDbSagaStoreDiagnostics)
            .GetMethod(nameof(querySagasAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType);

        var task = (Task<SagaQueryResult>)helper.Invoke(this, [sagaType, options, pageNumber, pageSize, ct])!;
        return await task.ConfigureAwait(false);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Saga criteria are refused through GetQueryCapabilities when dynamic code is unavailable.")]
    private async Task<SagaQueryResult> querySagasAsync<TSaga>(Type sagaType, DocumentQueryOptions options,
        int pageNumber, int pageSize, CancellationToken ct) where TSaga : class
    {
        var idMember = idMemberName(sagaType);
        var source = _container.GetItemLinqQueryable<TSaga>();

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
