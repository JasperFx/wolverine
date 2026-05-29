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
        writer.Write(
            $"{_message.FSharpAssignmentUsage} = {_envelope.Usage}.{nameof(Envelope.Message)} :?> {_message.VariableType.FSharpName()}");
        writer.BlankLine();
        Next?.GenerateFSharpCode(method, writer);
    }
}