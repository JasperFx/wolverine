using System;
using Lamar;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

public interface ISagaPersistenceFrameProvider
{
    Type DetermineSagaIdType(Type sagaType, IContainer container);
    Frame DetermineLoadFrame(IContainer container, Type sagaType, Variable sagaId);
    Frame DetermineInsertFrame(Variable saga, IContainer container);
    Frame CommitUnitOfWorkFrame(Variable saga, IContainer container);
    Frame DetermineUpdateFrame(Variable saga, IContainer container);
    Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IContainer container);
}
