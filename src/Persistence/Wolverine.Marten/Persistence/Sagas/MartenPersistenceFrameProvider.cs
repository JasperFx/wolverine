using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using Marten.Metadata;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Marten.Persistence.Sagas;

internal class MartenPersistenceFrameProvider : IPersistenceFrameProvider
{
    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IDocumentSession);
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        var store = container.GetInstance<IDocumentStore>();
        return store.Options.FindOrResolveDocumentType(sagaType).IdType;
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

        if (chain.ReturnVariablesOfType<IMartenOp>().Any()) return true;

        var serviceDependencies = chain
            .ServiceDependencies(container, new []{typeof(IDocumentSession), typeof(IQuerySession)}).ToArray();
        return serviceDependencies.Any(x => x == typeof(IDocumentSession) || x.Closes(typeof(IEventStream<>)));
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
        if (saga.VariableType.CanBeCastTo<IRevisioned>())
        {
            return new UpdateSagaRevisionFrame(saga);
        }
        
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Update));
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Delete));
    }

    public Frame DetermineStoreFrame(Variable variable, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(variable, nameof(IDocumentSession.Store));
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return new DocumentSessionOperationFrame(variable, nameof(IDocumentSession.Delete));
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var method = typeof(MartenStorageActionApplier).GetMethod("ApplyAction")
            .MakeGenericMethod(entityType);

        var call = new MethodCall(typeof(MartenStorageActionApplier), method);
        call.Arguments[1] = action;

        return call;
    }

}

public static class MartenStorageActionApplier
{
    public static void ApplyAction<T>(IDocumentSession session, IStorageAction<T> action)
    {
        if (action.Entity == null) return;
        
        switch (action.Action)
        {
            case StorageAction.Delete:
                session.Delete(action.Entity!);
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
    public DocumentSessionSaveChanges() : base(typeof(IDocumentSession), ReflectionHelper.GetMethod<IDocumentSession>(x => x.SaveChangesAsync(default)))
    {
        CommentText = "Save all pending changes to this Marten session";
    }
}