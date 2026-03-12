using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Code generation frame that wraps handler calls with a ResequencerSaga.ShouldProceed() guard.
/// The saga is always persisted (because Pending may change), but handler calls only execute
/// if ShouldProceed returns true.
/// </summary>
internal class ShouldProceedGuardFrame : Frame
{
    private readonly Variable _saga;
    private readonly Variable _message;
    private readonly Frame[] _innerFrames;
    private Variable? _context;

    public ShouldProceedGuardFrame(Variable saga, Variable message, Frame[] innerFrames) : base(true)
    {
        _saga = saga;
        _message = message;
        _innerFrames = innerFrames;
        uses.Add(_saga);
        uses.Add(_message);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        foreach (var inner in _innerFrames)
        {
            foreach (var variable in inner.FindVariables(chain))
            {
                yield return variable;
            }
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Resequencer guard - check message ordering");
        writer.Write(
            $"var shouldProceed = await {_saga.Usage}.{nameof(ResequencerSaga<SequencedMessage>.ShouldProceed)}({_message.Usage}, {_context!.Usage}).ConfigureAwait(false);");
        writer.Write("BLOCK:if (shouldProceed)");

        foreach (var inner in _innerFrames)
        {
            inner.GenerateCode(method, writer);
        }

        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}
