using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

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
        writer.Write(
            $"var {_message.Usage} = ({_message.VariableType.FullNameInCode()}){_envelope.Usage}.{nameof(Envelope.Message)};");
        Next?.GenerateCode(method, writer);
    }
}