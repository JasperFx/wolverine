using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Sagas;

internal class AssertSagaStateExistsFrame : SyncFrame
{
    private readonly Variable _sagaId;
    private readonly Variable _sagaState;

    public AssertSagaStateExistsFrame(Variable sagaState, Variable sagaId)
    {
        _sagaState = sagaState;
        _sagaId = sagaId;
        uses.Add(_sagaState);
        uses.Add(_sagaId);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"throw new {typeof(UnknownSagaException)}(typeof({_sagaState.VariableType.FullNameInCode()}), {_sagaId.Usage});");
        Next?.GenerateCode(method, writer);
    }
}