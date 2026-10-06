using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.InMemory.Codegen;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.InMemory;

/// <summary>
/// The in-memory prototyping store's half of Wolverine's persistence and event sourcing seams
/// (wolverine#4838). Both interfaces on one class, as Marten, Polecat and Fisher do, so the aggregate
/// handler workflow finds it through the persistence strategies already registered on GenerationRules.
/// </summary>
/// <remarks>
/// Only Wolverine's store-agnostic usage is supported: <c>[WriteModel]</c> / <c>[ReadModel]</c>,
/// <c>Storage.StartStream</c> / <c>AppendEvents</c>, <c>[Entity]</c> and <c>IStorageAction&lt;T&gt;</c> /
/// <c>UnitOfWork&lt;T&gt;</c>, plus sagas. There is no durable inbox or outbox: the session commits on its
/// own, and outgoing messages are flushed after it.
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Prototyping store; not AOT-compatible.")]
internal class InMemoryPersistenceFrameProvider : IPersistenceFrameProvider, IEventSourcingFrameProvider
{
    // ---- IPersistenceFrameProvider ----

    // A document store persists any type, so yield to selective providers (EF Core) for the entity types
    // they actually map, whatever order the integrations were registered in
    public bool IsCatchAll => true;

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IInMemoryDocumentSession);
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        var idProp = sagaType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        return idProp?.PropertyType ?? typeof(Guid);
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (!chain.Middleware.OfType<CreateInMemorySessionFrame>().Any())
        {
            chain.Middleware.Add(new CreateInMemorySessionFrame(chain));
        }

        if (chain is SagaChain) return;

        if (!chain.Postprocessors.OfType<InMemorySessionSaveChanges>().Any())
        {
            chain.Postprocessors.Add(new InMemorySessionSaveChanges());
        }

        if (!chain.Postprocessors.OfType<FlushOutgoingMessages>().Any())
        {
            chain.Postprocessors.Add(new FlushOutgoingMessages());
        }
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
        => ApplyTransactionSupport(chain, container);

    public bool OwnsStorageType(Type storageType, IServiceContainer container)
        => storageType == typeof(InMemoryDocumentStore) || storageType.CanBeCastTo<IInMemoryQuerySession>();

    // The parameter types that mean a chain writes through this store. A read-only parameter
    // (IDocumentReadOperations, IInMemoryQuerySession) is not evidence that the chain writes anything.
    private static readonly Type[] WritingDependencies =
    [
        typeof(IInMemoryDocumentSession), typeof(IDocumentSessionOperations), typeof(IDocumentWriteOperations),
        typeof(IEventStoreOperations), typeof(IEventOperations)
    ];

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain) return true;

        var dependencies = chain
            .ServiceDependencies(container, [.. WritingDependencies, typeof(IInMemoryQuerySession), typeof(IDocumentReadOperations)])
            .ToArray();

        return dependencies.Any(x => WritingDependencies.Contains(x) || x.Closes(typeof(IEventStream<>)));
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
        => new LoadDocumentFrame(sagaType, sagaId);

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
        => new DocumentOperationFrame(saga, nameof(IDocumentWriteOperations.Store));

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container) => new InMemorySessionSaveChanges();

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
        => new DocumentOperationFrame(saga, nameof(IDocumentWriteOperations.Store));

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
        => new DocumentOperationFrame(saga, nameof(IDocumentWriteOperations.Delete));

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
        => new DocumentOperationFrame(saga, nameof(IDocumentWriteOperations.Store));

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
        => new DocumentOperationFrame(variable, nameof(IDocumentWriteOperations.Delete));

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var method = typeof(InMemoryStorageActionApplier).GetMethod(nameof(InMemoryStorageActionApplier.ApplyAction))!
            .MakeGenericMethod(entityType);

        var call = new MethodCall(typeof(InMemoryStorageActionApplier), method);
        call.Arguments[1] = action;

        return call;
    }

    public bool TryBuildAllFrame(Type entityType, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame, [NotNullWhen(true)] out Variable? result)
        => query(entityType, QueryFrame.Shape.All, out frame, out result);

    public bool TryBuildFirstOrDefaultFrame(Type entityType, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame, [NotNullWhen(true)] out Variable? result)
        => query(entityType, QueryFrame.Shape.FirstOrDefault, out frame, out result);

    public bool TryBuildQueryableFrame(Type elementType, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame, [NotNullWhen(true)] out Variable? result)
        => query(elementType, QueryFrame.Shape.Queryable, out frame, out result);

    private static bool query(Type entityType, QueryFrame.Shape shape, out Frame frame, out Variable result)
    {
        var query = new QueryFrame(entityType, shape);
        frame = query;
        result = query.Result;
        return true;
    }

    // No soft-delete metadata is exposed through the session, so there's nothing to null out
    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];

    // ---- IEventSourcingFrameProvider ----

    public string StoreName => "In-memory prototyping store";

    public Type UnknownAggregateExceptionType => typeof(UnknownAggregateException);

    public Frame BuildLoadAggregateFrame(AggregateLoadRequest request) => new LoadAggregateFrame(request);

    public Frame BuildFetchLatestFrame(Type aggregateType, Variable identity)
        => new FetchLatestAggregateFrame(aggregateType, identity);

    public StreamIdentity DetermineStreamIdentity(IServiceContainer container)
        => container.Services.GetRequiredService<InMemoryDocumentStore>().Events.StreamIdentity;
}
