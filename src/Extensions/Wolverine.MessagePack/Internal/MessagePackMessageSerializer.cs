using MessagePack;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MessagePack.Internal;

internal class MessagePackMessageSerializer : IMessageSerializer
{
    private readonly MessagePackSerializerOptions _options;

    public MessagePackMessageSerializer(MessagePackSerializerOptions options)
    {
        _options = options;
    }

    public string ContentType => "binary/messagepack";

    public byte[] Write(Envelope envelope)
    {
        return envelope.Message is null
            ? throw new NullReferenceException("Envelope message is empty")
            : MessagePackSerializer.Serialize(envelope.Message.GetType(), envelope.Message, _options);
    }

    public byte[] WriteMessage(object message)
    {
        return MessagePackSerializer.Serialize(message.GetType(), message, _options);
    }

    public object ReadFromData(byte[] data)
    {
        // TODO: Maybe we should't allow this?
        return MessagePackSerializer.Deserialize(typeof(object), data, _options)!;
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return MessagePackSerializer.Deserialize(messageType, envelope.Data, _options)!;
    }
}
