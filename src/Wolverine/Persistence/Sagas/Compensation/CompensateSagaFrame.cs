using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5556. In a saga's <c>Fault&lt;T&gt;</c> chain, runs the saga's <c>Compensate</c> method that matches the failure,
/// between loading the saga and saving it
/// </summary>
internal class CompensateSagaFrame : AsyncFrame
{
    private readonly Variable _saga;
    private readonly Type _faultType;
    private Variable? _fault;
    private Variable? _context;

    public CompensateSagaFrame(Variable saga, Type faultType)
    {
        _saga = saga;
        _faultType = faultType;
        uses.Add(saga);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _fault = chain.FindVariable(_faultType);
        yield return _fault;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var failedType = SagaCompensations.FaultMessageType(_faultType)!;
        writer.WriteComment("Compensate the failed message with the saga's best matching Compensate method");
        writer.Write(
            $"await {typeof(SagaCompensations).FullNameInCode()}.{nameof(SagaCompensations.CompensateAsync)}<{_saga.VariableType.FullNameInCode()}, {failedType.FullNameInCode()}>({_saga.Usage}, {_fault!.Usage}, {_context!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}
