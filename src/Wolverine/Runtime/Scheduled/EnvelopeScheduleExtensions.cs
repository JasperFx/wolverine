using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Scheduled;

internal static class EnvelopeScheduleExtensions
{
    public static Envelope ForScheduledSend(this Envelope envelope, ISendingAgent? sender)
    {
        return new Envelope(envelope, EnvelopeReaderWriter.Instance)
        {
            Message = envelope,
            MessageType = TransportConstants.ScheduledEnvelope,
            ScheduledTime = envelope.ScheduledTime,
            ContentType = TransportConstants.SerializedEnvelope,
            Destination = TransportConstants.DurableLocalUri,
            Status = EnvelopeStatus.Scheduled,
            OwnerId = TransportConstants.AnyNode,
            Sender = sender,
            Data = EnvelopeSerializer.Serialize(envelope)
        };
    }
}