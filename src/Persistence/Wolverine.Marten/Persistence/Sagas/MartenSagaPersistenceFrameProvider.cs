using System;
using System.Linq;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Lamar;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Marten;
using Wolverine.Marten.Codegen;
using Wolverine.Persistence;

namespace Wolverine.Marten.Persistence.Sagas;

public class MartenSagaPersistenceFrameProvider : ISagaPersistenceFrameProvider, ITransactionFrameProvider
{
    public Type DetermineSagaIdType(Type sagaType, IContainer container)
    {
        var store = container.GetInstance<IDocumentStore>();
        return store.Options.FindOrResolveDocumentType(sagaType).IdType;
    }

    public Frame DetermineLoadFrame(IContainer container, Type sagaType, Variable sagaId)
    {
        return new LoadDocumentFrame(sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Insert));
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IContainer container)
    {
        return MethodCall.For<IDocumentSession>(x => x.SaveChangesAsync(default));
    }

    public Frame DetermineUpdateFrame(Variable saga, IContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Update));
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IContainer container)
    {
        return new DocumentSessionOperationFrame(saga, nameof(IDocumentSession.Delete));
    }

    public void ApplyTransactionSupport(IChain chain, IContainer container)
    {
        if (!chain.Middleware.OfType<TransactionalFrame>().Any())
        {
            chain.Middleware.Add(new TransactionalFrame());
        }
    }
}
