using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

public class InMemoryPersistenceFrameProvider : IPersistenceFrameProvider
{
    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        // Nothing
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        return false;
    }

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = GetType();
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        return SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetMemberType() ?? typeof(object);
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
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

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
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

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        return new CommentFrame("No unit of work");
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return DetermineInsertFrame(saga, container);
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
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