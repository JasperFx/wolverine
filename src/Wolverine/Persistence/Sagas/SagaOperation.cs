using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

public class SagaOperation : AsyncFrame, ISagaOperation
{
    private Variable _storage;
    private Variable _cancellation;

    public SagaOperation(Variable saga, SagaOperationType operation)
    {
        Saga = saga;
        Operation = operation;
        uses.Add(saga);
    }

    public Variable Saga { get; }

    public SagaOperationType Operation { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _storage = chain.FindVariable(typeof(ISagaStorage<>).MakeGenericType(Saga.VariableType));
        yield return _storage;
        
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"await {_storage.Usage}.{Operation}({Saga.Usage}, {_cancellation.Usage});");
        Next?.GenerateCode(method, writer);
    }
}