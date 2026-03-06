using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;
using Polecat.Events;
using Wolverine.Configuration;
using Wolverine.Polecat.Codegen;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Persistence.Sagas;

internal class PolecatPersistenceFrameProvider : IPersistenceFrameProvider
{
    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IDocumentSession);
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        var idProp = sagaType.GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return idProp?.PropertyType ?? typeof(Guid);
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

        var serviceDependencies = chain
            .ServiceDependencies(container, new[] { typeof(IDocumentSession), typeof(IQuerySession), typeof(IDocumentOperations) }).ToArray();
        return serviceDependencies.Any(x => x == typeof(IDocumentSession) || x == typeof(IDocumentOperations) || x.Closes(typeof(IEventStream<>)));
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
        var method = typeof(PolecatStorageActionApplier).GetMethod("ApplyAction")
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
        CommentText = "Save all pending changes to this Polecat session";
    }
}
