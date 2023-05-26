using System;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

public interface IPersistenceFrameProvider
{
    void ApplyTransactionSupport(IChain chain, IContainer container);
    bool CanApply(IChain chain, IContainer container);

    /// <summary>
    /// Use for Saga creation support as returned value
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="container"></param>
    /// <param name="persistenceService"></param>
    /// <returns></returns>
    bool CanPersist(Type entityType, IContainer container, out Type persistenceService);
    
    Type DetermineSagaIdType(Type sagaType, IContainer container);
    Frame DetermineLoadFrame(IContainer container, Type sagaType, Variable sagaId);
    Frame DetermineInsertFrame(Variable saga, IContainer container);
    Frame CommitUnitOfWorkFrame(Variable saga, IContainer container);
    Frame DetermineUpdateFrame(Variable saga, IContainer container);
    Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IContainer container);
}