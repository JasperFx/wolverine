using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime;

/// <summary>
/// Codegen frame inserted between the handler return-value frames and the
/// postprocessor frames when
/// <c>WolverineOptions.Tracking.EnableMessageCausationTracking</c> is set at
/// codegen time. Emits a call to the generated handler's inherited
/// <c>MessageHandler.RecordCauseAndEffect(MessageContext, IWolverineObserver, string)</c>
/// so that <c>IWolverineObserver.MessageCausedBy</c> is reported once per
/// unique (incoming, outgoing) pair after handler execution but before the
/// outbox flush.
///
/// The handler type name is baked into the generated code as a string literal,
/// resolved at codegen time from the chain's handler calls by the same helper
/// that builds the <c>message.handler</c> span tag (TagHandlerPolicy), so both
/// observation paths name the user's handler rather than the generated
/// <c>MessageHandler</c> subclass. See GH-5224.
///
/// When the flag is off the frame isn't emitted at all and the framework
/// <c>Executor</c> never calls into the cause-and-effect path — no runtime
/// <c>if/then</c>. See GH-2694.
/// </summary>
internal class RecordMessageCausationFrame : Frame
{
    private readonly string _handlerTypeName;
    private Variable? _context;

    public RecordMessageCausationFrame(string handlerTypeName) : base(false)
    {
        _handlerTypeName = handlerTypeName;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // Use the MessageContext that the generated handler already has in scope —
        // it carries Runtime.Observer for the cause-and-effect emission.
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    private string arguments()
    {
        var args = $"{_context!.Usage}, {_context!.Usage}.Runtime.Observer";

        // No handler calls to name (should not happen for a generated handler chain): fall back to the
        // two-argument overload, which reports the generated class name as it always has.
        return _handlerTypeName.IsEmpty() ? args : $"{args}, \"{_handlerTypeName}\"";
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Generated as an unqualified instance call on the generated handler
        // class (which extends MessageHandler). The observer comes from
        // context.Runtime.Observer; MessageBus.Runtime is the public accessor.
        writer.Write($"{nameof(MessageHandler.RecordCauseAndEffect)}({arguments()});");

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // RecordCauseAndEffect is an inherited instance method on the generated MessageHandler subclass,
        // so it must be qualified with the member's `this` self identifier (jasperfx#393).
        writer.Write($"this.{nameof(MessageHandler.RecordCauseAndEffect)}({arguments()})");

        Next?.GenerateFSharpCode(method, writer);
    }
}
