using JasperFx.Core.Reflection;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Runtime.Scheduled;

internal class EnvelopeReaderWriter : IMessageSerializer
{
    public static IMessageSerializer Instance { get; } = new EnvelopeReaderWriter();
    public string ContentType => TransportConstants.SerializedEnvelope;

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        if (messageType != typeof(Envelope))
        {
            throw new ArgumentOutOfRangeException(nameof(messageType), "This serializer only supports envelopes");
        }

        return ReadFromData(envelope.Data!);
    }

    public object ReadFromData(byte[] data)
    {
        var envelope = EnvelopeSerializer.Deserialize(data);

        return envelope;
    }

    public byte[] WriteMessage(object message)
    {
        throw new NotSupportedException();
    }

    public byte[] Write(Envelope model)
    {
        return EnvelopeSerializer.Serialize(model.As<Envelope>());
    }
}