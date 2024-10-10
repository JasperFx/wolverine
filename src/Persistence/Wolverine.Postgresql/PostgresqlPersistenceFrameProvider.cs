using System.Data.Common;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Npgsql;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Postgresql;

internal class PostgresqlPersistenceFrameProvider : IPersistenceFrameProvider
{
    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (chain.Middleware.OfType<DbTransactionFrame<NpgsqlTransaction, NpgsqlConnection>>().Any()) return;
        
        var shouldFlushOutgoingMessages = chain.ShouldFlushOutgoingMessages();
        
        var frame = new DbTransactionFrame<NpgsqlTransaction, NpgsqlConnection>
            { ShouldFlushOutgoingMessages = shouldFlushOutgoingMessages };

        chain.Middleware.Add(frame);
        
        if (chain is SagaChain)
        {
            chain.Middleware.Add(new CodeFrame(false, $"var dbtx = ({typeof(DbTransaction).FullNameInCode()}){{0}};", frame.Transaction));
        }
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain)
        {
            return true;
        }

        return chain.ServiceDependencies(container, Type.EmptyTypes)
            .Any(x => x == typeof(NpgsqlConnection) || x == typeof(NpgsqlTransaction));
    }

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        if (entityType.CanBeCastTo<Saga>())
        {
            persistenceService = typeof(IDatabaseSagaStorage);
            return true;
        }
        
        persistenceService = default!;
        return false;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        var storageDefinition = new SagaTableDefinition(sagaType, null);
        return storageDefinition.IdMember.GetMemberType();
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        return new LoadSagaOperation(sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        return new SagaOperation(saga, SagaOperationType.InsertAsync);
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        return new CommentFrame("No additional Unit of Work necessary");
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return new SagaOperation(saga, SagaOperationType.UpdateAsync);
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new SagaOperation(saga, SagaOperationType.DeleteAsync);
    }
}