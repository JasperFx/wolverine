using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

public interface IPersistenceFrameProvider
{
    void ApplyTransactionSupport(IChain chain, IServiceContainer container);
    void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType);
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
    
    /// <summary>
    /// Create an "upsert" Frame for the variable. Not every persistence provider will be able to support this
    /// and should throw NotSupportedException if it does not
    /// </summary>
    /// <param name="saga"></param>
    /// <param name="container"></param>
    /// <returns></returns>
    Frame DetermineStoreFrame(Variable saga, IServiceContainer container);

    /// <summary>
    /// Create a delete Frame for the variable, not every persistence provider will be able to support this
    /// and should throw NotSupportedException if it does not
    /// </summary>
    /// <param name="variable"></param>
    /// <param name="container"></param>
    /// <returns></returns>
    Frame DetermineDeleteFrame(Variable variable, IServiceContainer container);

    Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container);

    Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity);
}



