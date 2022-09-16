using System;
using Lamar;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Oakton.Parsing;

namespace Wolverine.Persistence.Sagas;

public class InMemorySagaPersistenceFrameProvider : ISagaPersistenceFrameProvider
{
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
