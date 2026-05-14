using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

public class SagaOperation : AsyncFrame, ISagaOperation
{
    private Variable _storage = null!;
    private Variable _cancellation = null!;

    public SagaOperation(Variable saga, SagaOperationType operation)
    {
        Saga = saga;
        Operation = operation;
        uses.Add(saga);
    }

    public Variable Saga { get; }

    public SagaOperationType Operation { get; }

    // ISagaStorage<> closed over runtime saga type at codegen time. Same
    // Dynamic-mode rationale as the saga frame providers (chunk P) — AOT-clean
    // apps run pre-generated frames in TypeLoadMode.Static.
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "ISagaStorage<> closed over runtime saga type during Dynamic codegen; AOT consumers register saga types explicitly. See AOT guide.")]
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