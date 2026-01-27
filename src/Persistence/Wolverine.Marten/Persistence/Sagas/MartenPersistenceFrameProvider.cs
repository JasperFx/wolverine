using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using Marten.Storage.Metadata;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using IRevisioned = Marten.Metadata.IRevisioned;

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
        var documentType = store.Options.FindOrResolveDocumentType(sagaType);

        return documentType.IdType;
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
            .ServiceDependencies(container, new []{typeof(IDocumentSession), typeof(IQuerySession), typeof(IDocumentOperations)}).ToArray();
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
        var method = typeof(MartenStorageActionApplier).GetMethod("ApplyAction")
            .MakeGenericMethod(entityType);

        var call = new MethodCall(typeof(MartenStorageActionApplier), method);
        call.Arguments[1] = action;

        return call;
    }

    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)
    {
        return [new SetVariableToNullIfSoftDeletedFrame(entity)];
    }
}

internal class SetVariableToNullIfSoftDeletedFrame : AsyncFrame
{
    private Variable _entity;
    private Variable _documentSession;
    private Variable _entityMetadata;

    public SetVariableToNullIfSoftDeletedFrame(Variable entity)
    {
        _entity = entity;
        uses.Add(entity);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("If the document is soft deleted, set the variable to null");

        writer.Write($"var {_entityMetadata.Usage} = {_entity.Usage} != null");
        writer.Write($"    ? await {_documentSession.Usage}.{nameof(IDocumentSession.MetadataForAsync)}({_entity.Usage}).ConfigureAwait(false)");
        writer.Write($"    : null;");
            
        writer.Write($"BLOCK:if ({_entityMetadata.Usage}?.{nameof(DocumentMetadata.Deleted)} == true)");
        writer.Write($"{_entity.Usage} = null;");
        writer.FinishBlock();
            
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _documentSession = chain.FindVariable(typeof(IDocumentSession));
        yield return _documentSession;

        _entityMetadata = new Variable(typeof(DocumentMetadata), _entity.Usage + "Metadata", this);
        yield return _entityMetadata;
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