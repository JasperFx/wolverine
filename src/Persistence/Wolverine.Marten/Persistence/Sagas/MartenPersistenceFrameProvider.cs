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
        if (!chain.Middleware.OfType<TransactionalFrame>().Any())
        {
            chain.Middleware.Add(new TransactionalFrame(chain));

            if (chain is not SagaChain)
            {
                var saveChanges = MethodCall.For<IDocumentSession>(x => x.SaveChangesAsync(default));
                saveChanges.CommentText = "Commit any outstanding Marten changes";
                chain.Postprocessors.Add(saveChanges);

                var methodCall = MethodCall.For<MessageContext>(x => x.FlushOutgoingMessagesAsync());
                methodCall.CommentText = "Have to flush outgoing messages just in case Marten did nothing because of https://github.com/JasperFx/wolverine/issues/536";

                chain.Postprocessors.Add(methodCall);
            }
        }
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
        var call = MethodCall.For<IDocumentSession>(x => x.SaveChangesAsync(default));
        call.CommentText = "Commit all pending changes";
        return call;
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
}