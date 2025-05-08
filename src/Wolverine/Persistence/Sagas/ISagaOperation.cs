using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

public interface ISagaOperation
{
    Variable Saga { get; }
    SagaOperationType Operation { get; }
}