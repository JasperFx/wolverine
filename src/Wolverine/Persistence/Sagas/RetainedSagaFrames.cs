using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     GH-5572. The saga lookup of a retained saga type. A completed saga is still in storage, so the generated code
///     has three branches instead of two: no saga, a completed saga, and a live saga. Only a live saga reaches the
///     saga's handler methods.
/// </summary>
internal class RetainedSagaGuardFrame : Frame
{
    private readonly Frame[] _completedPath;
    private readonly Frame[] _existsPath;
    private readonly Frame[] _nullPath;
    private readonly Variable _saga;

    public RetainedSagaGuardFrame(Variable saga, Frame[] nullPath, Frame[] completedPath, Frame[] existsPath)
        : base(nullPath.Concat(completedPath).Concat(existsPath).Any(x => x.IsAsync))
    {
        _saga = saga;
        _nullPath = nullPath;
        _completedPath = completedPath;
        _existsPath = existsPath;
        uses.Add(saga);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"BLOCK:if ({_saga.Usage} == null)");
        foreach (var frame in _nullPath) frame.GenerateCode(method, writer);
        writer.FinishBlock();

        writer.WriteComment("A retained saga that has already completed is treated like one that no longer exists");
        writer.Write($"BLOCK:else if ({_saga.Usage}.{nameof(Saga.IsCompleted)}())");
        foreach (var frame in _completedPath) frame.GenerateCode(method, writer);
        writer.FinishBlock();

        writer.Write("BLOCK:else");
        foreach (var frame in _existsPath) frame.GenerateCode(method, writer);
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"BLOCK:if isNull (box {_saga.FSharpUsage}) then");
        foreach (var frame in _nullPath) frame.GenerateFSharpCode(method, writer);
        writer.FinishBlock();

        writer.Write($"BLOCK:elif {_saga.FSharpUsage}.{nameof(Saga.IsCompleted)}() then");
        foreach (var frame in _completedPath) frame.GenerateFSharpCode(method, writer);
        writer.FinishBlock();

        writer.Write("BLOCK:else");
        foreach (var frame in _existsPath) frame.GenerateFSharpCode(method, writer);
        writer.FinishBlock();

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var path in new[] { _nullPath, _completedPath, _existsPath })
        {
            foreach (var frame in path)
            {
                foreach (var variable in frame.FindVariables(chain))
                {
                    if (path.Any(x => x.Creates.Contains(variable))) continue;
                    yield return variable;
                }
            }
        }
    }
}

/// <summary>
///     GH-5572. A message that would start a saga, for the id of a retained saga that has already completed, and no
///     <c>NotFound</c> method for it. The completed saga keeps its id, so the message is not handled; it is logged
///     instead.
/// </summary>
internal class SkipStartOfCompletedSagaFrame : SyncFrame
{
    private readonly Type _sagaType;
    private readonly Variable _sagaId;
    private Variable? _context;

    public SkipStartOfCompletedSagaFrame(Type sagaType, Variable sagaId)
    {
        _sagaType = sagaType;
        _sagaId = sagaId;
        uses.Add(sagaId);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(SagaRetention).FullNameInCode()}.{nameof(SagaRetention.SkipStartOfCompletedSaga)}({_context!.Usage}, typeof({_sagaType.FullNameInCode()}), {_sagaId.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(SagaRetention).FSharpName()}.{nameof(SagaRetention.SkipStartOfCompletedSaga)}({_context!.Usage}, typeof<{_sagaType.FSharpName()}>, box {_sagaId.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}
