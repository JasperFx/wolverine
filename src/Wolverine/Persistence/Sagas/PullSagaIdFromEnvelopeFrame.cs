using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Sagas;

internal class PullSagaIdFromEnvelopeFrame : SyncFrame
{
    private readonly Type _sagaType;
    private Variable? _envelope;

    public PullSagaIdFromEnvelopeFrame(Type sagaType, Type sagaIdType)
    {
        _sagaType = sagaType;
        SagaId = new Variable(sagaIdType, SagaChain.SagaIdVariableName, this);
    }

    public Variable SagaId { get; }

    /// <summary>
    /// GH-4531: this frame is the "the message has no identity member" path, so the failure message says
    /// the envelope header was the only source there was. A null member name selects that wording.
    /// </summary>
    private string csharpDiagnosticArguments => $"typeof({_sagaType.FullNameInCode()}), null";

    private string fsharpDiagnosticArguments => $"typeof<{_sagaType.FSharpName()}>, null";

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (SagaId.VariableType == typeof(string))
        {
            writer.Write($"var {SagaId.Usage} = {_envelope!.Usage}.{nameof(Envelope.SagaId)};");
            writer.Write(
                $"if (string.{nameof(string.IsNullOrEmpty)}({SagaChain.SagaIdVariableName})) throw new {typeof(IndeterminateSagaStateIdException).FullName}({_envelope.Usage}, {csharpDiagnosticArguments});");
        }
        else
        {
            var typeNameInCode = SagaId.VariableType == typeof(Guid)
                ? typeof(Guid).FullName
                : SagaId.VariableType.FullNameInCode();

            writer.Write(
                $"if (!{typeNameInCode}.TryParse({_envelope!.Usage}.{nameof(Envelope.SagaId)}, out {typeNameInCode} sagaId)) throw new {typeof(IndeterminateSagaStateIdException).FullName}({_envelope.Usage}, {csharpDiagnosticArguments});");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var id = SagaChain.SagaIdVariableName;
        var ex = typeof(IndeterminateSagaStateIdException).FSharpName();
        var envSagaId = $"{_envelope!.Usage}.{nameof(Envelope.SagaId)}";

        if (SagaId.VariableType == typeof(string))
        {
            writer.Write($"let {id} = {envSagaId}");
            writer.Write($"BLOCK:if System.String.IsNullOrEmpty({id}) then");
            writer.Write($"raise ({ex}({_envelope.Usage}, {fsharpDiagnosticArguments}))");
            writer.FinishBlock();
        }
        else
        {
            // F# auto-tuples the out-parameter TryParse; bind the id from the match, raising on failure.
            var clrType = SagaId.VariableType.FullName; // System.Guid / System.Int64 — valid static call target
            writer.Write($"BLOCK:let {id} =");
            writer.Write($"BLOCK:match {clrType}.TryParse({envSagaId}) with");
            writer.Write("| true, parsed -> parsed");
            writer.Write($"| _ -> raise ({ex}({_envelope.Usage}, {fsharpDiagnosticArguments}))");
            writer.FinishBlock();
            writer.FinishBlock();
        }

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _envelope = chain.FindVariable(typeof(Envelope));
        yield return _envelope;
    }
}