using Azure.Messaging.ServiceBus;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Internal;

internal class AzureServiceBusEnvelopeMapper : EnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage>, IAzureServiceBusEnvelopeMapper
{
    public AzureServiceBusEnvelopeMapper(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint)
    {
        MapProperty(x => x.ContentType!, (e, m) => e.ContentType = m.ContentType,
            (e, m) => m.ContentType = e.ContentType);
        MapProperty(x => x.Data!, (e, m) => e.Data = m.Body.ToArray(), (e, m) => m.Body = new BinaryData(e.Data!));
        MapProperty(x => x.Id, (e, m) =>
        {
            if (Guid.TryParse(m.MessageId, out var id))
            {
                e.Id = id;
            }
        }, (e, m) => m.MessageId = e.Id.ToString());

        MapProperty(x => x.CorrelationId!, (e, m) => e.CorrelationId = m.CorrelationId,
            (e, m) => m.CorrelationId = e.CorrelationId);
        MapProperty(x => x.MessageType!, (e, m) => e.MessageType = m.Subject, (e, m) => m.Subject = e.MessageType);
        MapProperty(x => x.ScheduledTime!, (_, _) =>
        {
            // Nothing
        }, (e, m) =>
        {
            if (e.ScheduledTime != null)
            {
                m.ScheduledEnqueueTime = e.ScheduledTime.Value.ToUniversalTime();
            }
        });
        
        MapProperty(x => x.GroupId, (e, m) => e.GroupId = m.SessionId, (e, m) => m.SessionId = e.GroupId);

    }

    protected override void writeOutgoingHeader(ServiceBusMessage outgoing, string key, string value)
    {
        outgoing.ApplicationProperties[key] = value;
    }

    protected override bool tryReadIncomingHeader(ServiceBusReceivedMessage incoming, string key, out string? value)
    {
        if (incoming.ApplicationProperties.TryGetValue(key, out var header))
        {
            value = header.ToString();
            return true;
        }

        value = default;
        return false;
    }
}