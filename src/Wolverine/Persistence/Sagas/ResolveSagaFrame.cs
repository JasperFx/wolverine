using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

internal class ResolveSagaFrame : Frame
{
    private readonly Frame _findSagaIdFrame;
    private readonly Frame _loadFrame;

    public ResolveSagaFrame(Frame findSagaIdFrame, Frame loadFrame) : base(loadFrame.IsAsync)
    {
        _findSagaIdFrame = findSagaIdFrame;
        _loadFrame = loadFrame;
        var innerSagaId = findSagaIdFrame.Creates.Single();
        SagaId = new Variable(innerSagaId.VariableType, innerSagaId.Usage, this);

        var innerSaga = loadFrame.Creates.Single();
        Saga = new Variable(innerSaga.VariableType, innerSaga.Usage, this);
    }

    public Variable SagaId { get; }
    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        return _findSagaIdFrame.FindVariables(chain).Union(_loadFrame.FindVariables(chain)).Distinct();
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        _findSagaIdFrame.GenerateCode(method, writer);
        _loadFrame.GenerateCode(method, writer);
        Next?.GenerateCode(method, writer);
    }
}