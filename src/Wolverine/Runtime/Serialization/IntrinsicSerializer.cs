using System.Reflection.Emit;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Serialization;


public class IntrinsicSerializer<T> : IMessageSerializer where T : ISerializable
{
    public string ContentType { get; } = "application/octet-stream";
    public byte[] Write(Envelope envelope)
    {
        return WriteMessage(envelope.Message);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return T.Read(envelope.Data);
    }

    public object ReadFromData(byte[] data)
    {
        return T.Read(data);
    }

    public byte[] WriteMessage(object message)
    {
        if (message is ISerializable s)
        {
            return s.Write();
        }

        throw new ArgumentOutOfRangeException(nameof(message),
            $"The message type {message.GetType().FullNameInCode()} does not implement {nameof(ISerializable)}");

    }
}