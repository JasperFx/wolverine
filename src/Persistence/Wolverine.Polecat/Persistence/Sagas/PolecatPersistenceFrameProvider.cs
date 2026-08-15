using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;
using JasperFx.Events;
using Polecat.Events;
using Wolverine.Configuration;
using Wolverine.Polecat.Codegen;
using Wolverine.Polecat.Requirements;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Persistence.Sagas;

internal partial class PolecatPersistenceFrameProvider : IPersistenceFrameProvider
{
    // Polecat can persist any document, so CanPersist claims every type. Yield to selective
    // providers (EF Core) for the entity types they actually map, regardless of the order the
    // integrations were registered in
    public bool IsCatchAll => true;

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IDocumentSession);
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        var idProp = sagaType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        if (idProp == null) return typeof(Guid);

        // GH-3942: unwrap Nullable<T>, so the two stores agree on an aggregate's id type.
        // Marten answers with the *configured document id type*, which is never nullable. Reflecting
        // the property verbatim answered Nullable<AlertId> for `public AlertId? Id { get; set; }`,
        // and WriteModelAttribute.FindIdentity treats that as non-primitive -- so it skipped the
        // IdentifiedBy<T> escape hatch entirely and went looking for a Nullable<AlertId> member on
        // the message, which exists nowhere. Every such chain failed to build under Polecat while
        // the identical source worked under Marten.
        return Nullable.GetUnderlyingType(idProp.PropertyType) ?? idProp.PropertyType;
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (!chain.Middleware.OfType<CreateDocumentSessionFrame>().Any())
        {
            chain.Middleware.Add(new CreateDocumentSessionFrame(chain));
        }

        if (chain is not SagaChain)
        {
            if (!chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any())
            {
                chain.Postprocessors.Add(new DocumentSessionSaveChanges());
            }

            if (!chain.Postprocessors.OfType<FlushOutgoingMessages>().Any())
            {
                chain.Postprocessors.Add(new FlushOutgoingMessages());
            }
        }
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        ApplyTransactionSupport(chain, container);
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain)
        {
            return true;
        }

        if (chain.ReturnVariablesOfType<IPolecatOp>().Any()) return true;

        // GH-2941: detect parameter attributes whose Modify() injects a non-MethodCall frame
        // depending on IDocumentSession. See MartenPersistenceFrameProvider.CanApply for the full
        // explanation; Polecat mirrors the Marten path structurally. Pairs with the upstream
        // Polecat fix (polecat#161, shipped in Polecat 4.2.1): DocumentSessionBase.SaveChangesAsync
        // now also runs queued ITransactionParticipants when there are no document operations
        // outstanding, so the StoreIncomingEnvelopeParticipant added via Session.StoreIncoming(...)
        // for a scheduled cascade actually executes inside the chain's session transaction.
        if (ChainHasPolecatSessionAttributes(chain)) return true;

        var serviceDependencies = chain
            .ServiceDependencies(container, new[] { typeof(IDocumentSession), typeof(IQuerySession), typeof(IDocumentOperations), typeof(global::JasperFx.Events.IEventOperations), typeof(global::JasperFx.Events.IEventStoreOperations), typeof(global::Polecat.Events.IEventOperations) }).ToArray();
                // A handler that takes the event operations straight as a parameter -- the shared
        // JasperFx.Events.IEventOperations / IEventStoreOperations, or Polecat's own IEventOperations -- is
        // unambiguously using this store, but none of those types appeared here, so
        // AutoApplyTransactions skipped the chain and nothing was ever committed. Appending
        // through the parameter queued into the session's unit of work and then silently
        // vanished, with no exception.
        return serviceDependencies.Any(x => x == typeof(IDocumentSession) || x == typeof(IDocumentOperations)
                                            || x.Closes(typeof(IEventStream<>))
                                            || x == typeof(global::JasperFx.Events.IEventOperations)
                                            || x == typeof(global::JasperFx.Events.IEventStoreOperations)
                                            || x == typeof(global::Polecat.Events.IEventOperations));
    }

    private static bool ChainHasPolecatSessionAttributes(IChain chain)
    {
        foreach (var call in chain.HandlerCalls())
        {
            foreach (var parameter in call.Method.GetParameters())
            {
                if (parameter.GetCustomAttributes().Any(a => a is ReadAggregateAttribute)) return true;
            }
        }

        foreach (var call in chain.HandlerCalls())
        {
            if (call.Method.GetCustomAttributes().Any(IsDocumentExistsAttribute)) return true;
            if (call.HandlerType.GetCustomAttributes(true).OfType<Attribute>().Any(IsDocumentExistsAttribute)) return true;
        }

        var messageType = chain.InputType();
        if (messageType != null && messageType.GetCustomAttributes(true).OfType<Attribute>().Any(IsDocumentExistsAttribute))
        {
            return true;
        }

        return false;
    }

    private static bool IsDocumentExistsAttribute(Attribute attribute)
    {
        var type = attribute.GetType();
        if (!type.IsGenericType) return false;
        var def = type.GetGenericTypeDefinition();
        return def == typeof(DocumentExistsAttribute<>) || def == typeof(DocumentDoesNotExistAttribute<>);
    }

    public bool TryBuildAllFrame(Type entityType, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame,
        [NotNullWhen(true)] out Variable? result)
    {
        var all = new AllFrame(entityType);
        frame = all;
        result = all.Result;
        return true;
    }

    public bool TryBuildFirstOrDefaultFrame(Type entityType, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame,
        [NotNullWhen(true)] out Variable? result)
    {
        var first = new FirstOrDefaultFrame(entityType);
        frame = first;
        result = first.Result;
        return true;
    }

    public bool TryBuildQueryableFrame(Type elementType, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame,
        [NotNullWhen(true)] out Variable? result)
    {
        var queryable = new QueryableFrame(elementType);
        frame = queryable;
        result = queryable.Result;
        return true;
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        return new LoadDocumentFrame(sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Insert));
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        return new DocumentSessionSaveChanges();
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Update));
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Delete));
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Store));
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(variable, nameof(IDocumentSession.Delete));
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var method = typeof(PolecatStorageActionApplier).GetMethod("ApplyAction")!
            .MakeGenericMethod(entityType);

        var call = new MethodCall(typeof(PolecatStorageActionApplier), method);
        call.Arguments[1] = action;

        return call;
    }

    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)
    {
        // Polecat doesn't have DocumentMetadata for soft-delete check in the same way,
        // so we skip this for now
        return [];
    }
}

public static class PolecatStorageActionApplier
{
    public static void ApplyAction<T>(IDocumentSession session, IStorageAction<T> action) where T : notnull
    {
        if (action.Entity == null) return;

        switch (action.Action)
        {
            case StorageAction.Delete:
                session.Delete(action.Entity);
                break;
            case StorageAction.Insert:
                session.Insert(action.Entity);
                break;
            case StorageAction.Store:
                session.Store(action.Entity);
                break;
            case StorageAction.Update:
                session.Update(action.Entity);
                break;
        }
    }
}

internal class DocumentSessionSaveChanges : MethodCall
{
    public DocumentSessionSaveChanges() : base(typeof(IDocumentSession), ReflectionHelper.GetMethod<IDocumentSession>(x => x.SaveChangesAsync(default))!)
    {
        CommentText = "Save all pending changes to this Polecat session";
    }
}
