using ImTools;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

public class NatsEnvelopeMapper : EnvelopeMapper<NatsMsg<byte[]>, NatsHeaders>
{
    // GH-4328: one Uri per received message was being parsed from an interpolated string, but the
    // subject on an incoming message is the listener's own subscribed subject — a small bounded
    // set. Reply subjects are deliberately NOT cached: request/reply uses ephemeral per-request
    // _INBOX.* subjects and caching those would grow without bound.
    private static ImHashMap<string, Uri> _subjectUris = ImHashMap<string, Uri>.Empty;

    internal static Uri UriForIncomingSubject(string subject)
    {
        if (_subjectUris.TryFind(subject, out var uri))
        {
            return uri;
        }

        uri = new Uri($"nats://subject/{subject}");
        _subjectUris = _subjectUris.AddOrUpdate(subject, uri);
        return uri;
    }

    private readonly ITenantSubjectMapper? _tenantMapper;
    
    public NatsEnvelopeMapper(NatsEndpoint endpoint, ITenantSubjectMapper? tenantMapper = null)
        : base(endpoint)
    {
        _tenantMapper = tenantMapper;
    }

    protected override void writeOutgoingHeader(NatsHeaders headers, string key, string value)
    {
        headers[key] = value;
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
        envelope.Destination = UriForIncomingSubject(incoming.Subject);

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
    private readonly ITenantSubjectMapper? _tenantMapper;
    
    public JetStreamEnvelopeMapper(NatsEndpoint endpoint, ITenantSubjectMapper? tenantMapper = null)
        : base(endpoint)
    {
        _tenantMapper = tenantMapper;
    }

    protected override void writeOutgoingHeader(NatsHeaders headers, string key, string value)
    {
        headers[key] = value;
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
        envelope.Destination = NatsEnvelopeMapper.UriForIncomingSubject(incoming.Subject);

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
