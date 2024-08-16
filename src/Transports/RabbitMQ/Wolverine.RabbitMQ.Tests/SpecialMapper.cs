using JasperFx.Core;
using RabbitMQ.Client;
using Wolverine.RabbitMQ.Internal;

namespace Wolverine.RabbitMQ.Tests;

#region sample_rabbit_special_mapper

public class SpecialMapper : IRabbitMqEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, IBasicProperties outgoing)
    {
        // All of this is default behavior, but this sample does show
        // what's possible here
        outgoing.CorrelationId = envelope.CorrelationId;
        outgoing.MessageId = envelope.Id.ToString();
        outgoing.ContentType = "application/json";

        if (envelope.DeliverBy.HasValue)
        {
            var ttl = Convert.ToInt32(envelope.DeliverBy.Value.Subtract(DateTimeOffset.Now).TotalMilliseconds);
            outgoing.Expiration = ttl.ToString();
        }

        if (envelope.TenantId.IsNotEmpty())
        {
            outgoing.Headers ??= new Dictionary<string, object>();
            outgoing.Headers["tenant-id"] = envelope.TenantId;
        }
    }

    public void MapIncomingToEnvelope(Envelope envelope, IReadOnlyBasicProperties incoming)
    {
        envelope.CorrelationId = incoming.CorrelationId;
        envelope.ContentType = "application/json";
        if (Guid.TryParse(incoming.MessageId, out var id))
        {
            envelope.Id = id;
        }
        else
        {
            envelope.Id = Guid.NewGuid();
        }

        if (incoming.Headers != null && incoming.Headers.TryGetValue("tenant-id", out var tenantId))
        {
            // Watch this in real life, some systems will send header values as
            // byte arrays
            envelope.TenantId = (string)tenantId;
        }
    }

    public IEnumerable<string> AllHeaders()
    {
        yield break;
    }
}

#endregion