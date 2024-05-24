using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Data.SqlClient;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.SqlServer;

internal class SqlServerPersistenceFrameProvider : IPersistenceFrameProvider
{
    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        var shouldFlushOutgoingMessages = chain.ShouldFlushOutgoingMessages();


        var frame = new DbTransactionFrame<SqlTransaction, SqlConnection>
            { ShouldFlushOutgoingMessages = shouldFlushOutgoingMessages };

        chain.Middleware.Add(frame);
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain)
        {
            return false;
        }

        return chain.ServiceDependencies(container, Type.EmptyTypes).Any(x => x == typeof(SqlConnection) || x == typeof(SqlTransaction));
    }

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = default!;
        return false;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        throw new NotSupportedException();
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException();
    }
}