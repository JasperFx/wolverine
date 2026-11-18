using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-4484. Resolves the saga id, and when the message arrived with none, runs the saga's <c>IdNotProvided</c> method
/// for it instead of throwing <see cref="IndeterminateSagaStateIdException" />. Otherwise loads the saga and carries on
/// exactly as a saga handler does without the hook.
/// </summary>
internal class SagaIdGuardFrame : Frame
{
    public const string MissingVariableName = "sagaIdIsMissing";

    private readonly Type _sagaType;
    private readonly Frame _findSagaId;
    private readonly Frame _load;
    private readonly Frame[] _idNotProvided;
    private Frame[] _sagaFound = [];

    public SagaIdGuardFrame(Type sagaType, Frame findSagaId, Frame load, Frame[] idNotProvided) : base(true)
    {
        _sagaType = sagaType;
        _findSagaId = findSagaId;
        _load = load;
        _idNotProvided = idNotProvided;

        var innerSagaId = findSagaId.Creates.First();
        SagaId = new Variable(innerSagaId.VariableType, innerSagaId.Usage, this);

        var innerSaga = load.Creates.First();
        Saga = new Variable(innerSaga.VariableType, innerSaga.Usage, this);
    }

    public Variable SagaId { get; }
    public Variable Saga { get; }

    /// <summary>
    /// What runs once the saga id is known: the "saga exists" and "saga does not exist" branches
    /// </summary>
    public void WhenTheIdIsKnown(params Frame[] frames) => _sagaFound = frames;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        var mine = new HashSet<string> { SagaId.Usage, Saga.Usage };
        var inner = new[] { _findSagaId, _load }.Concat(_idNotProvided).Concat(_sagaFound).ToArray();
        var created = inner.SelectMany(x => x.Creates).ToHashSet();

        return inner
            .SelectMany(x => x.FindVariables(chain))
            .Where(x => !created.Contains(x) && x.Creator != this && !mine.Contains(x.Usage))
            .Distinct();
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        _findSagaId.GenerateCode(method, writer);

        writer.Write($"BLOCK:if ({MissingVariableName})");
        writer.WriteComment($"No saga id came with the message: {_sagaType.NameInCode()}.IdNotProvided() handles it");
        foreach (var frame in _idNotProvided) frame.GenerateCode(method, writer);
        writer.FinishBlock();

        writer.Write("BLOCK:else");
        _load.GenerateCode(method, writer);
        foreach (var frame in _sagaFound) frame.GenerateCode(method, writer);
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        throw new NotSupportedException(
            $"{_sagaType.FullNameInCode()} declares IdNotProvided(), which F# handler code generation does not support yet.");
    }
}
