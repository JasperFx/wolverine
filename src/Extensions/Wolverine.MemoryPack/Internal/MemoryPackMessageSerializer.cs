using MemoryPack;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MemoryPack.Internal;

internal class MemoryPackMessageSerializer : IMessageSerializer
{
    private readonly MemoryPackSerializerOptions _options;

    public MemoryPackMessageSerializer(MemoryPackSerializerOptions options)
    {
        _options = options;
    }

    public string ContentType => "binary/memorypack";

    public byte[] Write(Envelope envelope)
    {
        if (envelope.Message == null)
        {
            throw new NullReferenceException("Envelope message is empty");
        }

        return MemoryPackSerializer.Serialize(envelope.Message.GetType(), envelope.Message, _options);
    }

    public byte[] WriteMessage(object message)
    {
        return MemoryPackSerializer.Serialize(message.GetType(), message, _options);
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotSupportedException("MemoryPack requires a known message type");
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return MemoryPackSerializer.Deserialize(messageType, envelope.Data, _options)!;
    }
}