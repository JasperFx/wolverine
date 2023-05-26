using System;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;
using Oakton.Parsing;
using Wolverine.Configuration;

namespace Wolverine.Persistence.Sagas;

public class InMemoryPersistenceFrameProvider : IPersistenceFrameProvider
{
    public void ApplyTransactionSupport(IChain chain, IContainer container)
    {
        // Nothing
    }

    public bool CanApply(IChain chain, IContainer container)
    {
        return false;
    }

    public bool CanPersist(Type entityType, IContainer container, out Type persistenceService)
    {
        persistenceService = GetType();
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IContainer container)
    {
        return SagaChain.DetermineSagaIdMember(sagaType)?.GetMemberType() ?? typeof(object);
    }

    public Frame DetermineLoadFrame(IContainer container, Type sagaType, Variable sagaId)
    {
        var method = typeof(InMemorySagaPersistor).GetMethod(nameof(InMemorySagaPersistor.Load))!
            .MakeGenericMethod(sagaType);

        var call = new MethodCall(typeof(InMemorySagaPersistor), method)
        {
            Arguments =
            {
                [0] = sagaId
            }
        };

        return call;
    }

    public Frame DetermineInsertFrame(Variable saga, IContainer container)
    {
        var method = typeof(InMemorySagaPersistor).GetMethod(nameof(InMemorySagaPersistor.Store))!
            .MakeGenericMethod(saga.VariableType);
        var call = new MethodCall(typeof(InMemorySagaPersistor), method)
        {
            Arguments =
            {
                [0] = saga
            }
        };

        return call;
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IContainer container)
    {
        return new CommentFrame("No unit of work");
    }

    public Frame DetermineUpdateFrame(Variable saga, IContainer container)
    {
        return DetermineInsertFrame(saga, container);
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IContainer container)
    {
        var method = typeof(InMemorySagaPersistor).GetMethod(nameof(InMemorySagaPersistor.Delete))!
            .MakeGenericMethod(saga.VariableType);
        var call = new MethodCall(typeof(InMemorySagaPersistor), method)
        {
            Arguments =
            {
                [0] = sagaId
            }
        };

        return call;
    }
}