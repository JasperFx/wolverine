using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

public interface IPersistenceFrameProvider
{
    void ApplyTransactionSupport(IChain chain, IServiceContainer container);
    bool CanApply(IChain chain, IServiceContainer container);

    /// <summary>
    ///     Use for Saga creation support as returned value
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="container"></param>
    /// <param name="persistenceService"></param>
    /// <returns></returns>
    bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService);

    Type DetermineSagaIdType(Type sagaType, IServiceContainer container);
    Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId);
    Frame DetermineInsertFrame(Variable saga, IServiceContainer container);
    Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container);
    Frame DetermineUpdateFrame(Variable saga, IServiceContainer container);
    Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container);
}

public interface ISagaOperation
{
    Variable Saga { get; }
    SagaOperationType Operation { get; }
}

public enum SagaOperationType
{
    InsertAsync,
    UpdateAsync,
    DeleteAsync
}
