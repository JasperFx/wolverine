using NATS.Client.Core;
using NATS.Client.JetStream;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

public class NatsEnvelopeMapper : EnvelopeMapper<NatsMsg<byte[]>, NatsHeaders>
{
    private readonly Uri _destination;
    private readonly ITenantSubjectMapper? _tenantMapper;
    
    public NatsEnvelopeMapper(NatsEndpoint endpoint, ITenantSubjectMapper? tenantMapper = null)
        : base(endpoint)
    {
        _destination = endpoint.Uri;
        _tenantMapper = tenantMapper;
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
    
    public JetStreamEnvelopeMapper(NatsEndpoint endpoint, ITenantSubjectMapper? tenantMapper = null)
        : base(endpoint)
    {
        _destination = endpoint.Uri;
        _tenantMapper = tenantMapper;
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
