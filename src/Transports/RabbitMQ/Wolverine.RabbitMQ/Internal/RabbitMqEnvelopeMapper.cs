using System;
using System.Diagnostics;
using System.Text;
using JasperFx.Core;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

/// <summary>
/// Responsible for mapping incoming and outgoing Wolverine Envelope objects to the
/// Rabbit MQ IBasicProperties object. Custom implementations of this can be used
/// to create interoperability with non-Wolverine applications through Rabbit MQ
/// </summary>
public interface IRabbitMqEnvelopeMapper : IEnvelopeMapper<IBasicProperties, IBasicProperties>;

internal class RabbitMqEnvelopeMapper : EnvelopeMapper<IBasicProperties, IBasicProperties>, IRabbitMqEnvelopeMapper
{
    public RabbitMqEnvelopeMapper(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint)
    {
        MapProperty(x => x.CorrelationId!, (e, p) => e.CorrelationId = p.CorrelationId,
            (e, p) => p.CorrelationId = e.CorrelationId);
        MapProperty(x => x.ContentType!, (e, p) => e.ContentType = p.ContentType,
            (e, p) => p.ContentType = e.ContentType);

        Action<Envelope, IBasicProperties> readId = (e, props) =>
        {
            if (Guid.TryParse(props.MessageId, out var id))
            {
                e.Id = id;
            }
            else
            {
                // Might not be a real Guid coming from the outside world
                e.Id = CombGuidIdGeneration.NewGuid();
            }
        };
        
        MapProperty(x => x.Id, readId,
            (e, props) => props.MessageId = e.Id.ToString());

        MapProperty(x => x.DeliverBy!, (_, _) => { }, (e, props) =>
        {
            if (e.DeliverBy.HasValue)
            {
                var ttl = Convert.ToInt32(e.DeliverBy.Value.Subtract(DateTimeOffset.Now).TotalMilliseconds);
                props.Expiration = ttl.ToString();
            }
        });

        MapProperty(x => x.MessageType!, (e, props) => e.MessageType = props.Type,
            (e, props) => props.Type = e.MessageType);
    }

    protected override void writeOutgoingHeader(IBasicProperties outgoing, string key, string value)
    {
        outgoing.Headers[key] = value;
    }

    // TODO -- this needs to be open for customizations. See the NServiceBus interop
    protected override bool tryReadIncomingHeader(IBasicProperties incoming, string key, out string? value)
    {
        if (incoming.Headers == null)
        {
            value = null;
            return false;
        }
        
        if (incoming.Headers.TryGetValue(key, out var raw))
        {
            value = (raw is byte[] b ? Encoding.Default.GetString(b) : raw.ToString())!;
            return true;
        }

        value = null;
        return false;
    }

    protected override void writeIncomingHeaders(IBasicProperties incoming, Envelope envelope)
    {
        if (incoming.Headers == null) return;
        foreach (var pair in incoming.Headers)
        {
            envelope.Headers[pair.Key] =
                pair.Value is byte[] b ? Encoding.Default.GetString(b) : pair.Value?.ToString();
        }
    }
}