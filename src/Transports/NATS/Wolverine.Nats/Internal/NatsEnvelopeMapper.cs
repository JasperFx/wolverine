using NATS.Client.Core;
using NATS.Client.JetStream;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

public class NatsEnvelopeMapper : EnvelopeMapper<NatsMsg<byte[]>, NatsHeaders>
{
    private readonly ITenantSubjectMapper? _tenantMapper;

    // Named brokers carry the broker name as their URI scheme, so incoming envelopes must be stamped with
    // this endpoint's scheme (not a hard-coded "nats") for reply/tracking routing to resolve back to the
    // right transport instance. See AddNamedNatsBroker.
    private readonly string _scheme;

    public NatsEnvelopeMapper(NatsEndpoint endpoint, ITenantSubjectMapper? tenantMapper = null)
        : base(endpoint)
    {
        _tenantMapper = tenantMapper;
        _scheme = endpoint.Uri.Scheme;
    }

    protected override void writeOutgoingHeader(NatsHeaders headers, string key, string value)
    {
        headers[key] = value;
    }

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
        envelope.Destination = new Uri($"{_scheme}://subject/{incoming.Subject}");

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
                $"{_scheme}://subject/{incoming.ReplyTo}"
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

    // See NatsEnvelopeMapper._scheme — named brokers carry the broker name as their URI scheme.
    private readonly string _scheme;

    public JetStreamEnvelopeMapper(NatsEndpoint endpoint, ITenantSubjectMapper? tenantMapper = null)
        : base(endpoint)
    {
        _tenantMapper = tenantMapper;
        _scheme = endpoint.Uri.Scheme;
    }

    protected override void writeOutgoingHeader(NatsHeaders headers, string key, string value)
    {
        headers[key] = value;
    }

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
        envelope.Destination = new Uri($"{_scheme}://subject/{incoming.Subject}");

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
                $"{_scheme}://subject/{incoming.ReplyTo}"
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
