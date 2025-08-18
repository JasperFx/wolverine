using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Expressions;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Raven.Client.Documents.Session;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using MethodCall = JasperFx.CodeGeneration.Frames.MethodCall;

namespace Wolverine.RavenDb.Internals;

public class RavenDbPersistenceFrameProvider : IPersistenceFrameProvider
{
    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];
    
    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (!chain.Middleware.OfType<TransactionalFrame>().Any())
        {
            chain.Middleware.Add(new TransactionalFrame(chain));

            if (chain is not SagaChain)
            {
                var saveChanges = MethodCall.For<IAsyncDocumentSession>(x => x.SaveChangesAsync(default));
                saveChanges.CommentText = "Commit any outstanding RavenDb changes";
                chain.Postprocessors.Add(saveChanges);
                
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

        if (chain.ReturnVariablesOfType<IRavenDbOp>().Any()) return true;

        var serviceDependencies = chain
            .ServiceDependencies(container, new []{typeof(IAsyncDocumentSession)}.ToArray());
        return serviceDependencies.Any(x => x == typeof(IAsyncDocumentSession));
    }

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IAsyncDocumentSession);
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        return typeof(string);
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        return new LoadDocumentFrame(sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        var call = MethodCall.For<IAsyncDocumentSession>(x => x.StoreAsync(null, default));
        call.Arguments[0] = saga;
        return call;
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        var call = MethodCall.For<IAsyncDocumentSession>(x => x.SaveChangesAsync(default));
        call.CommentText = "Commit all pending changes";
        return call;
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        // TODO -- try to do this with just a comment frame
        var call = MethodCall.For<IAsyncDocumentSession>(x => x.StoreAsync(null, default));
        call.Arguments[0] = saga;
        return call;
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new DeleteDocumentFrame(saga);
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        return DetermineUpdateFrame(saga, container);
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return new DeleteDocumentFrame(variable);
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var method = typeof(RavenDbStorageActionApplier).GetMethod("ApplyAction")
            .MakeGenericMethod(entityType);

        var call = new MethodCall(typeof(RavenDbStorageActionApplier), method);
        call.Arguments[1] = action;

        return call;
    }
}

public static class RavenDbStorageActionApplier
{
    public static async Task ApplyAction<T>(IAsyncDocumentSession session, IStorageAction<T> action)
    {
        if (action.Entity == null) return;
        
        switch (action.Action)
        {
            case StorageAction.Delete:
                session.Delete(action.Entity!);
                break;
            case StorageAction.Insert:
            case StorageAction.Store:
            case StorageAction.Update:
                await session.StoreAsync(action.Entity);
                break;
        }
    }
}


internal class DeleteDocumentFrame : SyncFrame
{
    private readonly Variable _saga;
    private Variable _session;

    public DeleteDocumentFrame(Variable saga)
    {
        _saga = saga;
        uses.Add(_saga);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IAsyncDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_session.Usage}.Delete({_saga.Usage});");
        Next?.GenerateCode(method, writer);
    }
}