using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

internal interface ISagaStorageFrame
{
    Variable SimpleVariable { get; }

    Variable Variable { get; }
}