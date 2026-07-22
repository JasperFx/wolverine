using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

internal class MessageFrame : Frame
{
    private readonly Variable _envelope;
    private readonly MessageVariable _message;

    public MessageFrame(MessageVariable message, Variable envelope) : base(false)
    {
        _message = message;
        _envelope = envelope;
        uses.Add(envelope);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("The actual message body");
        writer.Write(
            $"var {_message.Usage} = ({_message.VariableType.FullNameInCode()}){_envelope.Usage}.{nameof(Envelope.Message)};");
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // F#: `envelope.Message` is `obj`, so the cast to the concrete message type is a dynamic
        // downcast (`:?>`): `let message = envelope.Message :?> SomeMessage`.
        writer.WriteComment("The actual message body");
        var binding = _message.IsReferenced ? _message.FSharpAssignmentUsage : $"let _{_message.Usage}";

        // _envelope.Usage may be a derived path like "context.Envelope". When any method argument
        // that forms the root of this path is unreferenced, WriteFSharpMethod will have prefixed it
        // with `_` (e.g. "context" → "_context"). Adjust the envelope expression to match so the
        // emitted body refers to the same identifier as the parameter declaration.
        var envelopeExpr = _envelope.FSharpUsage;
        foreach (var arg in method.Arguments)
        {
            if (!arg.IsReferenced && envelopeExpr.StartsWith(arg.Usage + "."))
            {
                envelopeExpr = "_" + arg.Usage + envelopeExpr.Substring(arg.Usage.Length);
                break;
            }
        }

        writer.Write(
            $"{binding} = {envelopeExpr}.{nameof(Envelope.Message)} :?> {_message.VariableType.FSharpName()}");
        writer.BlankLine();
        Next?.GenerateFSharpCode(method, writer);
    }
}