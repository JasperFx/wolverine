using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Microsoft.Data.SqlClient;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer;

internal class SqlServerPersistenceFrameProvider : IPersistenceFrameProvider
{
    public void ApplyTransactionSupport(IChain chain, IContainer container)
    {
        var shouldFlushOutgoingMessages = chain.ShouldFlushOutgoingMessages();


        var frame = new DbTransactionFrame<SqlTransaction, SqlConnection>
            { ShouldFlushOutgoingMessages = shouldFlushOutgoingMessages };

        chain.Middleware.Add(frame);
    }

    public bool CanApply(IChain chain, IContainer container)
    {
        if (chain is SagaChain)
        {
            return false;
        }

        return chain.ServiceDependencies(container, Type.EmptyTypes).Any(x => x == typeof(SqlConnection) || x == typeof(SqlTransaction));
    }

    public bool CanPersist(Type entityType, IContainer container, out Type persistenceService)
    {
        persistenceService = default;
        return false;
    }

    public Type DetermineSagaIdType(Type sagaType, IContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame DetermineLoadFrame(IContainer container, Type sagaType, Variable sagaId)
    {
        throw new NotSupportedException();
    }

    public Frame DetermineInsertFrame(Variable saga, IContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame DetermineUpdateFrame(Variable saga, IContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IContainer container)
    {
        throw new NotSupportedException();
    }
}