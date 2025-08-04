using Google.Protobuf;

using Wolverine.Runtime.Serialization;

namespace Wolverine.Protobuf.Internal;

internal class ProtobufMessageSerializer(ProtobufSerializerOptions options) : IMessageSerializer
{
    public string ContentType => "binary/protobuf";

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        if (envelope?.Data == null || envelope.Data.Length == 0)
        {
            throw new ArgumentNullException(nameof(envelope), "Envelope data cannot be null or empty");
        }

        if (!typeof(Google.Protobuf.IMessage).IsAssignableFrom(messageType))
        {
            throw new ArgumentException($"Type {messageType.FullName} must implement {nameof(Google.Protobuf.IMessage)}", nameof(messageType));
        }

        var instance = Activator.CreateInstance(messageType);

        if (instance is not Google.Protobuf.IMessage protobufMessage)
        {
            throw new ArgumentException($"Type {messageType.FullName} must implement {nameof(Google.Protobuf.IMessage)}", nameof(messageType));
        }

        return protobufMessage.Descriptor.Parser.ParseFrom(envelope.Data);
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotSupportedException("Protobuf deserialization requires message type information. Use the ReadFromData(Type, Envelope) overload instead.");
    }

    public byte[] Write(Envelope envelope)
    {
        if (envelope.Message is null)
        {
            throw new NullReferenceException("Envelope message is empty");
        }

        return WriteMessage(envelope.Message);
    }

    public byte[] WriteMessage(object message)
    {
        if (message is not Google.Protobuf.IMessage protobufMessage)
        {
            throw new ArgumentException($"Message must implement {nameof(Google.Protobuf.IMessage)}", nameof(message));
        }

        using (var stream = new MemoryStream())
        {
            protobufMessage.WriteTo(stream);
            return stream.ToArray();
        }
    }
}

