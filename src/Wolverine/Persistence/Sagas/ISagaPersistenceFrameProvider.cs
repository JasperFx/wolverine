using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     This must be implemented for new types of saga storage
/// </summary>
public interface ISagaPersistenceFrameProvider
{
    Type DetermineSagaIdType(Type sagaType, IContainer container);
    Frame DetermineLoadFrame(IContainer container, Type sagaType, Variable sagaId);
    Frame DetermineInsertFrame(Variable saga, IContainer container);
    Frame CommitUnitOfWorkFrame(Variable saga, IContainer container);
    Frame DetermineUpdateFrame(Variable saga, IContainer container);
    Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IContainer container);
}