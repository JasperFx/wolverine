using NATS.Client.Core;
using NATS.Client.JetStream;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

public class NatsEnvelopeMapper : EnvelopeMapper<NatsMsg<byte[]>, NatsHeaders>
{
    private readonly Uri _destination;
    private readonly ITenantSubjectMapper? _tenantMapper;
    private readonly IncomingTenancy _tenancy;
    
    public NatsEnvelopeMapper(NatsEndpoint endpoint, ITenantSubjectMapper? tenantMapper = null)
        : base(endpoint)
    {
        _destination = endpoint.Uri;
        _tenantMapper = tenantMapper;
        _tenancy = new IncomingTenancy(endpoint, tenantMapper);
    }

    /// <summary>
    /// <see cref="EnvelopeMapper{TIncoming,TOutgoing}.MapIncomingToEnvelope"/>, and then -- unless the transport
    /// trusts the <c>tenant-id</c> header -- the tenant id from where the message arrived
    /// </summary>
    internal void MapIncoming(Envelope envelope, NatsMsg<byte[]> incoming)
    {
        MapIncomingToEnvelope(envelope, incoming);
        _tenancy.Apply(envelope, incoming.Subject);
    }

    protected override void writeOutgoingHeader(NatsHeaders headers, string key, string value)
    {
        headers[key] = NatsHeaderValues.Sanitize(value);
    }

    // GH-4328: writeIncomingHeaders copies every wire header into Envelope.Headers with the same
    // keys and stringification the per-property reader would produce, so let the ~20 reserved
    // property reads answer from the copied dictionary instead of re-probing (and re-stringifying
    // from) the transport message on every one. Same opt-in RabbitMQ and Kafka took in GH-3490/92.
    protected override bool preferCopiedIncomingHeaders => true;

    protected override bool tryReadIncomingHeader(
        NatsMsg<byte[]> incoming,
        string key,
        out string? value
    )
    {
        value = null;

        if (incoming.Headers == null)
        {
            return false;
        }

        if (incoming.Headers.TryGetValue(key, out var values))
        {
            value = values.ToString();
            return true;
        }

        return false;
    }

    protected override void writeIncomingHeaders(NatsMsg<byte[]> incoming, Envelope envelope)
    {
        envelope.Data = incoming.Data;

        // GH-4328: no Uri parsed per received message. The listener's own address is what the receiving
        // pipeline stamps on the envelope anyway (Envelope.MarkReceived). A Uri per incoming subject, cached
        // statically, grew by one entry for every distinct subject under a wildcard listener and for every reply
        // to an InvokeAsync() answered over core NATS, whose reply subject carries a per-request token.
        envelope.Destination = _destination;

        if (_tenantMapper != null)
        {
            var tenantId = _tenantMapper.ExtractTenantId(incoming.Subject);
            if (tenantId != null)
            {
                envelope.TenantId = tenantId;
            }
        }

        if (!string.IsNullOrEmpty(incoming.ReplyTo))
        {
            EnvelopeSerializer.ReadDataElement(
                envelope,
                EnvelopeConstants.ReplyUriKey,
                $"nats://subject/{incoming.ReplyTo}"
            );
        }

        if (incoming.Headers != null)
        {
            foreach (var header in incoming.Headers)
            {
                envelope.Headers[header.Key] = header.Value;
            }
        }
    }
}

public class JetStreamEnvelopeMapper : EnvelopeMapper<INatsJSMsg<byte[]>, NatsHeaders>
{
    private readonly Uri _destination;
    private readonly ITenantSubjectMapper? _tenantMapper;
    private readonly IncomingTenancy _tenancy;
    
    public JetStreamEnvelopeMapper(NatsEndpoint endpoint, ITenantSubjectMapper? tenantMapper = null)
        : base(endpoint)
    {
        _destination = endpoint.Uri;
        _tenantMapper = tenantMapper;
        _tenancy = new IncomingTenancy(endpoint, tenantMapper);
    }

    /// <summary>
    /// <see cref="EnvelopeMapper{TIncoming,TOutgoing}.MapIncomingToEnvelope"/>, and then -- unless the transport
    /// trusts the <c>tenant-id</c> header -- the tenant id from where the message arrived
    /// </summary>
    internal void MapIncoming(Envelope envelope, INatsJSMsg<byte[]> incoming)
    {
        MapIncomingToEnvelope(envelope, incoming);
        _tenancy.Apply(envelope, incoming.Subject);
    }

    /// <summary>
    /// Builds the envelope for a consumed JetStream message. With an <paramref name="identity" />, a message the
    /// mapping gave no id of its own -- it carries no Wolverine <c>id</c> header, and no mapping set one -- gets the
    /// id of its place in the stream, so a redelivery of it is the same envelope to the durable inbox.
    /// </summary>
    internal NatsEnvelope CreateEnvelope(INatsJSMsg<byte[]> incoming, JetStreamMessageIdentity? identity)
    {
        var envelope = new NatsEnvelope(null, incoming);

        // A fresh envelope's id is a new IdGenerator() value nobody else has seen, so the id still being that value
        // after mapping means neither the id header nor any other mapping supplied one
        var provisionalId = envelope.Id;

        MapIncoming(envelope, incoming);

        // A missing or unreadable id header is mapped to Guid.Empty instead, which the durable receiver replaces
        // with a fresh IdGenerator() value just before the inbox insert. Neither is an id.
        if (identity != null && incoming.Metadata is { } metadata &&
            (envelope.Id == provisionalId || envelope.Id == Guid.Empty))
        {
            envelope.Id = identity.IdFor(metadata.Sequence.Stream);
        }

        return envelope;
    }

    protected override void writeOutgoingHeader(NatsHeaders headers, string key, string value)
    {
        headers[key] = NatsHeaderValues.Sanitize(value);
    }

    // GH-4328: writeIncomingHeaders copies every wire header into Envelope.Headers with the same
    // keys and stringification the per-property reader would produce, so let the ~20 reserved
    // property reads answer from the copied dictionary instead of re-probing (and re-stringifying
    // from) the transport message on every one. Same opt-in RabbitMQ and Kafka took in GH-3490/92.
    protected override bool preferCopiedIncomingHeaders => true;

    protected override bool tryReadIncomingHeader(
        INatsJSMsg<byte[]> incoming,
        string key,
        out string? value
    )
    {
        value = null;

        if (incoming.Headers == null)
        {
            return false;
        }

        if (incoming.Headers.TryGetValue(key, out var values))
        {
            value = values.ToString();
            return true;
        }

        return false;
    }

    protected override void writeIncomingHeaders(INatsJSMsg<byte[]> incoming, Envelope envelope)
    {
        envelope.Data = incoming.Data;

        // The listener's own address; see NatsEnvelopeMapper
        envelope.Destination = _destination;

        if (_tenantMapper != null)
        {
            var tenantId = _tenantMapper.ExtractTenantId(incoming.Subject);
            if (tenantId != null)
            {
                envelope.TenantId = tenantId;
            }
        }

        if (!string.IsNullOrEmpty(incoming.ReplyTo))
        {
            EnvelopeSerializer.ReadDataElement(
                envelope,
                EnvelopeConstants.ReplyUriKey,
                $"nats://subject/{incoming.ReplyTo}"
            );
        }

        if (incoming.Metadata != null)
        {
            var metadata = incoming.Metadata.Value;
            envelope.Headers["nats-stream"] = metadata.Stream;
            envelope.Headers["nats-consumer"] = metadata.Consumer;
            envelope.Headers["nats-delivered"] = metadata.NumDelivered.ToString();
            envelope.Headers["nats-pending"] = metadata.NumPending.ToString();
            envelope.Headers["nats-stream-seq"] = metadata.Sequence.Stream.ToString();
            envelope.Headers["nats-consumer-seq"] = metadata.Sequence.Consumer.ToString();
        }

        if (incoming.Headers != null)
        {
            foreach (var header in incoming.Headers)
            {
                envelope.Headers[header.Key] = header.Value;
            }
        }
    }
}

/// <summary>
/// GH-4860. A NATS header value is one line of the wire protocol, so NATS.Net refuses a value holding a
/// carriage return. The dead letter copy of a message stamps the exception's stack trace into its
/// <c>exception-stack</c> header, and on Windows a stack trace is CRLF-delimited -- so every dead letter
/// forward from a Windows host failed, and the poison message was terminated without its copy. Line breaks
/// are folded to a bare LF, which the protocol carries, so the trace stays readable on the other side.
/// </summary>
internal static class NatsHeaderValues
{
    public static string Sanitize(string value)
    {
        if (value.IndexOf('\r') < 0)
        {
            return value;
        }

        return value.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}

/// <summary>
/// <see cref="Configuration.NatsTransportConfiguration.TrustTenantIdHeader"/> = false on a tenant-aware listener: the
/// tenant id the <c>tenant-id</c> header set gives way to the configured tenant the subject names, or to none. A
/// listener on a tenant's own connection stamps that tenant afterwards anyway.
/// </summary>
internal sealed class IncomingTenancy
{
    private readonly ITenantSubjectMapper? _tenantMapper;
    private readonly HashSet<string>? _tenantIds;

    public IncomingTenancy(NatsEndpoint endpoint, ITenantSubjectMapper? tenantMapper)
    {
        if (tenantMapper == null || endpoint.TrustsTenantIdHeader)
        {
            return;
        }

        _tenantMapper = tenantMapper;
        _tenantIds = endpoint.TenantIds();
    }

    public void Apply(Envelope envelope, string subject)
    {
        if (_tenantMapper == null)
        {
            return;
        }

        // Only a configured tenant counts: the default mapper reads the first token of any subject with a dot, so
        // on a plain "orders.created" it would name a tenant "orders"
        var tenantId = _tenantMapper.ExtractTenantId(subject);
        envelope.TenantId = tenantId != null && _tenantIds!.Contains(tenantId) ? tenantId : null;
    }
}
