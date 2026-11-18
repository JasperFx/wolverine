using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bobcat;
using Bobcat.CritterStack;
using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Bobcat;

// GH-5553 / GH-5556: saga state steps with their own wording, through the saga's own persistence -- the document
// store's, the lightweight saga tables', the in-memory store's -- so they are not tied to a document store (decision 3
// in SPEC-REVIEW-5553.md).
public partial class WolverineScenario
{
    private static readonly JsonSerializerOptions SagaJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// GH-5556. A saga is already in the state the act starts from: <paramref name="saga" />, stored through the
    /// persistence Wolverine uses for <typeparamref name="TSaga" />. Reads "an Order saga #OrderId with ...".
    /// </summary>
    /// <remarks>
    /// <paramref name="saga" /> may be a partial object. Works for a saga kept in a document store (Marten, Polecat,
    /// Fisher), in the lightweight saga tables of a relational message store, or in memory. Any other saga storage is
    /// refused: arrange that saga with the messages that start it instead.
    /// </remarks>
    public async Task GivenSaga<TSaga>(object saga) where TSaga : Saga
    {
        var label = $"{nameOf(typeof(TSaga))} saga";
        var built = Build(saga, $"{withArticle(label)}") as TSaga
                    ?? throw new SpecCriticalException(
                        $"'{withArticle(label)}': a {nameOf(PartialMatching.ExpectedType(saga))} cannot be stored as {withArticle(label)}.");

        var id = sagaIdOf(built);
        var values = saga is IPartialObject partial ? PartialObjects.DescribeValues(partial) : ScenarioValues.DescribeDocument(built);
        var inline = values.Length <= ScenarioValues.InlineLimit;
        var named = $"{withArticle(nameOf(typeof(TSaga)))} saga {ScenarioValues.Format(id)}";
        var text = inline && values.Length > 0 ? $"{named} with {values}" : named;

        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            if (!inline) recordValues("saga", [built]);
            recordTouched(typeof(TSaga));
            await storeSagaAsync(built, id, text);
        });
    }

    /// <summary>
    /// GH-5556. The <typeparamref name="TSaga" /> saga with this identity, loaded through the persistence Wolverine
    /// uses for it, judged against <paramref name="expected" />: a partial object (<c>Specify&lt;TSaga&gt;()</c>) on
    /// only the members it names, or a whole object structurally. Reads "the Order saga #OrderId matches". Fails when
    /// there is no such saga, and hands the saga back.
    /// </summary>
    public Task<TSaga> ThenSaga<TSaga>(object sagaId, object expected) where TSaga : Saga
        => thenSaga<TSaga>(sagaId, expected);

    /// <summary>
    /// GH-5556. The <typeparamref name="TSaga" /> saga with this identity exists, loaded through the persistence
    /// Wolverine uses for it. Reads "there is an Order saga #OrderId".
    /// </summary>
    public Task<TSaga> ThenSaga<TSaga>(object sagaId) where TSaga : Saga => thenSaga<TSaga>(sagaId, null);

    private Task<TSaga> thenSaga<TSaga>(object sagaId, object? expected) where TSaga : Saga
    {
        var named = $"{nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)}";
        var text = expected is null ? $"there is {withArticle(named)}" : $"the {named} matches";

        return inSagaStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            var saga = await loadSagaAsync<TSaga>(sagaId, text)
                       ?? throw failureOf($"No {nameOf(typeof(TSaga))} saga with id {sagaId}: it was completed, or never started.");

            recordTouched(typeof(TSaga));
            if (expected is not null) judge(saga, expected);
            return saga;
        });
    }

    /// <summary>
    /// GH-5556. No <typeparamref name="TSaga" /> saga with this identity exists: it was completed, or never started.
    /// Reads "there is no Order saga #OrderId".
    /// </summary>
    public async Task ThenNoSaga<TSaga>(object sagaId) where TSaga : Saga
    {
        var text = $"there is no {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)}";
        await inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            if (!acted)
            {
                Verdicts.Fail($"Expected no {nameOf(typeof(TSaga))} saga with id {sagaId}, but no act has run.");
                return;
            }

            var saga = await loadSagaAsync<TSaga>(sagaId, text);
            Verdicts.Fact(saga is null,
                $"Expected no {nameOf(typeof(TSaga))} saga with id {sagaId}, but there is one: {ScenarioValues.Describe(saga)}");
        });
    }

    // ---- proposed vocabulary for GH-5556 (spec review of 2026-11-18): renders, then fails ------------------------

    /// <summary>
    /// GH-5556, PROPOSED, NOT BUILT YET. The <typeparamref name="TSaga" /> saga with this identity has
    /// <paramref name="status" />, loaded through the saga's own persistence as <see cref="ThenSaga{TSaga}(object, object)" />
    /// loads it. Reads "the Order saga #OrderId has status Compensated".
    /// </summary>
    public void ThenTheSagaStatusIs<TSaga>(object sagaId, SagaStatus status) where TSaga : Saga, ICompensatingSaga
        => notBuilt("Then", $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} has status {status}",
            "proposed Bobcat vocabulary: ICompensatingSaga.Status, loaded as ThenSaga loads the saga");

    /// <summary>
    /// GH-5556, PROPOSED, NOT BUILT YET. The <typeparamref name="TSaga" /> saga with this identity compensated its failed
    /// <typeparamref name="TMessage" /> for a <typeparamref name="TException" /> or a subclass: its compensation log has
    /// that entry, with the outcome <see cref="CompensationOutcome.Compensated" />. Reads
    /// "the Order saga #OrderId compensates ReserveInventory for OutOfStockException".
    /// </summary>
    public void ThenTheSagaCompensated<TSaga, TMessage, TException>(object sagaId)
        where TSaga : Saga, ICompensatingSaga where TException : Exception
        => notBuilt("Then",
            $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} compensates {nameOf(typeof(TMessage))} for {nameOf(typeof(TException))}",
            "proposed Bobcat vocabulary: an ICompensatingSaga.CompensationLog entry for the message type and the exception type");

    /// <summary>
    /// GH-5556, PROPOSED, NOT BUILT YET. The <typeparamref name="TSaga" /> saga with this identity tried to compensate its
    /// failed <typeparamref name="TMessage" /> for a <typeparamref name="TException" />, and that compensation failed: its
    /// compensation log has that entry, with the outcome <see cref="CompensationOutcome.Failed" />. Reads
    /// "the Order saga #OrderId fails to compensate PaymentReceived for PaymentAmountMismatchException".
    /// </summary>
    public void ThenTheSagaFailedToCompensate<TSaga, TMessage, TException>(object sagaId)
        where TSaga : Saga, ICompensatingSaga where TException : Exception
        => notBuilt("Then",
            $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} fails to compensate {nameOf(typeof(TMessage))} for {nameOf(typeof(TException))}",
            "proposed Bobcat vocabulary: an ICompensatingSaga.CompensationLog entry with the outcome Failed");

    private static async Task<T> inSagaStep<T>(IDisposable step, Func<Task<T>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception e)
        {
            (step as IStepHandle)?.Fail(e);
            throw;
        }
        finally
        {
            step.Dispose();
        }
    }

    // "an Order saga", "a Shipment saga" (wolverine#5609)
    private static string withArticle(string noun)
        => (noun.Length > 0 && "AEIOUaeiou".Contains(noun[0]) ? "an " : "a ") + noun;

    private IWolverineRuntime sagaRuntime => Services.GetRequiredService<IWolverineRuntime>();

    private enum SagaPersistence
    {
        InMemory,
        Lightweight,
        DocumentStore,
        Other
    }

    // Which of Wolverine's saga storages owns TSaga: the same precedence the saga's handler code generation uses
    private (SagaPersistence Kind, IPersistenceFrameProvider? Provider) sagaPersistenceFor(Type sagaType)
    {
        var container = Services.GetRequiredService<IServiceContainer>();
        if (!sagaRuntime.Options.CodeGeneration.TryFindPersistenceFrameProvider(container, sagaType, out var provider))
        {
            // Nothing registered: Wolverine keeps sagas in memory
            return (SagaPersistence.InMemory, null);
        }

        return provider switch
        {
            InMemoryPersistenceFrameProvider => (SagaPersistence.InMemory, provider),
            LightweightSagaPersistenceFrameProvider => (SagaPersistence.Lightweight, provider),
            _ when provider.CanPersist(sagaType, container, out var service) && typeof(IDocumentReadOperations).IsAssignableFrom(service)
                => (SagaPersistence.DocumentStore, provider),
            _ => (SagaPersistence.Other, provider)
        };
    }

    private async Task storeSagaAsync<TSaga>(TSaga saga, object id, string step) where TSaga : Saga
    {
        var (kind, provider) = sagaPersistenceFor(typeof(TSaga));
        switch (kind)
        {
            case SagaPersistence.InMemory:
                Services.GetRequiredService<InMemorySagaPersistor>().Store(saga);
                break;

            case SagaPersistence.DocumentStore:
                await DocumentStores.StoreAllAsync(await storeForAsync(step, typeof(TSaga)), typeof(TSaga), [saga]);
                break;

            case SagaPersistence.Lightweight:
                await withLightweightStorage<TSaga, bool>(provider!, async (storage, _) =>
                {
                    await storage.InsertAsync(saga, CancellationToken.None);
                    await storage.SaveChangesAsync(CancellationToken.None);
                    return true;
                }, id);
                break;

            default:
                throw new SpecCriticalException(
                    $"'{step}': the {nameOf(typeof(TSaga))} saga is kept by {provider!.GetType().NameInCode()}, which this step cannot " +
                    "write to. Arrange the saga with the message that starts it instead.");
        }
    }

    private async Task<TSaga?> loadSagaAsync<TSaga>(object id, string step) where TSaga : Saga
    {
        var (kind, provider) = sagaPersistenceFor(typeof(TSaga));
        switch (kind)
        {
            case SagaPersistence.InMemory:
                return Services.GetRequiredService<InMemorySagaPersistor>().Load<TSaga>(id);

            case SagaPersistence.DocumentStore:
                return await EventStoreAuthoring.LoadDocumentAsync<TSaga>(await storeForAsync(step, typeof(TSaga)), id);

            case SagaPersistence.Lightweight:
                return await withLightweightStorage<TSaga, TSaga?>(provider!, loadLightweightAsync, id);

            default:
            {
                // EF Core, RavenDb, CosmosDb, Redis...: read through the storage's own saga diagnostics
                var typeName = typeof(TSaga).FullNameInCode();
                var registered = await sagaRuntime.SagaStorage.GetRegisteredSagasAsync(CancellationToken.None);
                if (!registered.Any(x => x.StateType.FullName == typeName || x.StateType.FullName == typeof(TSaga).FullName))
                    throw new SpecCriticalException(
                        $"'{step}': the {nameOf(typeof(TSaga))} saga is kept by {provider!.GetType().NameInCode()}, which has no saga diagnostics this step can read through.");

                var state = await sagaRuntime.SagaStorage.ReadSagaAsync(typeName, id, CancellationToken.None);
                return state?.State.Deserialize<TSaga>(SagaJson);
            }
        }
    }

    // The lightweight saga tables: through ISagaSupport on the message store, closed over the saga's id type
    private Task<TResult> withLightweightStorage<TSaga, TResult>(IPersistenceFrameProvider provider,
        Func<ISagaStorage<TSaga>, object, Task<TResult>> work, object id) where TSaga : Saga
    {
        var idType = provider.DetermineSagaIdType(typeof(TSaga), Services.GetRequiredService<IServiceContainer>());
        var method = typeof(WolverineScenario).GetMethod(nameof(withLightweightStorageOf), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(idType, typeof(TSaga), typeof(TResult));
        return (Task<TResult>)method.Invoke(null, [sagaRuntime, work, coerceSagaId(id, idType)])!;
    }

    private static async Task<TResult> withLightweightStorageOf<TId, TSaga, TResult>(IWolverineRuntime runtime,
        Func<ISagaStorage<TSaga>, object, Task<TResult>> work, object id) where TSaga : Saga
    {
        var storage = await SagaSupport<TId, TSaga>.EnrollAndFetchSagaStorage(new MessageContext(runtime));
        await using (storage.ConfigureAwait(false))
        {
            return await work(storage, id);
        }
    }

    private static async Task<TSaga?> loadLightweightAsync<TSaga>(ISagaStorage<TSaga> storage, object id) where TSaga : Saga
    {
        // ISagaStorage<TId, TSaga>.LoadAsync, with the id already coerced to TId
        var load = storage.GetType().GetInterfaces()
            .First(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(ISagaStorage<,>))
            .GetMethod("LoadAsync")!;
        return await (Task<TSaga?>)load.Invoke(storage, [id, CancellationToken.None])!;
    }

    private static object coerceSagaId(object id, Type idType)
    {
        if (idType.IsInstanceOfType(id)) return id;
        if (idType == typeof(Guid)) return Guid.Parse(id.ToString()!);
        if (idType == typeof(string)) return id.ToString()!;
        return Convert.ChangeType(id, idType, System.Globalization.CultureInfo.InvariantCulture);
    }

    // A saga's own identity: its Id member, as Wolverine's saga storage reads it
    private static object sagaIdOf(Saga saga)
    {
        var type = saga.GetType();
        var member = (MemberInfo?)type.GetProperty("Id") ?? type.GetField("Id");
        var id = member switch
        {
            PropertyInfo p => p.GetValue(saga),
            FieldInfo f => f.GetValue(saga),
            _ => null
        };

        return id ?? throw new SpecCriticalException(
            $"The {nameOf(type)} saga has no Id, so it cannot be arranged: give it an Id member and set it.");
    }
}
