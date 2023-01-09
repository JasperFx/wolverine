using MemoryPack;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MemoryPack.Internal;

internal class MemoryPackMessageSerializer : IMessageSerializer
{
    private readonly MemoryPackSerializerOptions _options;
    public string ContentType => "binary/memorypack";

    public MemoryPackMessageSerializer(MemoryPackSerializerOptions options)
    {
        _options = options;
    }

    public byte[] Write(Envelope envelope)
    {
        return MemoryPackSerializer.Serialize(envelope.Message, _options);
    }

    public byte[] WriteMessage(Object message)
    {
        return MemoryPackSerializer.Serialize(message, _options);
    }

    public object ReadFromData(Byte[] data)
    {
        throw new NotSupportedException("MemoryPack requires a known message type");
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return MemoryPackSerializer.Deserialize(messageType, envelope.Data, _options)!;
    }
}