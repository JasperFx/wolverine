using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime;

/// <summary>
/// Codegen frame inserted between the handler return-value frames and the
/// postprocessor frames when
/// <c>WolverineOptions.Tracking.EnableMessageCausationTracking</c> is set at
/// codegen time. Emits a call to the generated handler's inherited
/// <c>MessageHandler.RecordCauseAndEffect(MessageContext, IWolverineObserver)</c>
/// so that <c>IWolverineObserver.MessageCausedBy</c> is reported once per
/// unique (incoming, outgoing) pair after handler execution but before the
/// outbox flush.
///
/// When the flag is off the frame isn't emitted at all and the framework
/// <c>Executor</c> never calls into the cause-and-effect path — no runtime
/// <c>if/then</c>. See GH-2694.
/// </summary>
internal class RecordMessageCausationFrame : Frame
{
    private Variable? _context;

    public RecordMessageCausationFrame() : base(false)
    {
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // Use the MessageContext that the generated handler already has in scope —
        // it carries Runtime.Observer for the cause-and-effect emission.
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Generated as an unqualified instance call on the generated handler
        // class (which extends MessageHandler). The observer comes from
        // context.Runtime.Observer; MessageBus.Runtime is the public accessor.
        writer.Write(
            $"{nameof(MessageHandler.RecordCauseAndEffect)}({_context!.Usage}, {_context!.Usage}.Runtime.Observer);");

        Next?.GenerateCode(method, writer);
    }
}
