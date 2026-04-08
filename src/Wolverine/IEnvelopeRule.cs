using JasperFx.Core;
using Wolverine.Util;

namespace Wolverine;

/// <summary>
///     Intercepts outgoing messages and alters the parameters or
///     metadata of the message before sending to the outgoing brokers
/// </summary>
public interface IEnvelopeRule
{
    void Modify(Envelope envelope);

    public void ApplyCorrelation(IMessageContext originator, Envelope outgoing)
    {
        Modify(outgoing);
    }
}

internal class MessageTypeRule : IEnvelopeRule
{
    private readonly Type _messageType;
    private readonly string _messageTypeName;

    public MessageTypeRule(Type messageType)
    {
        _messageType = messageType;
        _messageTypeName = messageType.ToMessageTypeName();
    }

    public void Modify(Envelope envelope)
    {
        envelope.MessageType = _messageTypeName;
    }

    public override string ToString()
    {
        return $"{nameof(_messageType)} is {_messageType}, with MessageTypeName: {_messageTypeName}";
    }
}

internal class TenantIdRule : IEnvelopeRule
{
    public string TenantId { get; }

    public TenantIdRule(string tenantId)
    {
        TenantId = tenantId;
    }

    public void Modify(Envelope envelope)
    {
        envelope.TenantId = TenantId;
    }

    public override string ToString()
    {
        return $"{nameof(TenantId)}: {TenantId}";
    }
}

internal class DeliverWithinRule : IEnvelopeRule
{
    public DeliverWithinRule(TimeSpan time)
    {
        Time = time;
    }

    public TimeSpan Time { get; }

    public void Modify(Envelope envelope)
    {
        envelope.DeliverWithin = Time;
    }

    public override string ToString()
    {
        return $"Time to live: {Time}";
    }

    protected bool Equals(DeliverWithinRule other)
    {
        return Time.Equals(other.Time);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((DeliverWithinRule)obj);
    }

    public override int GetHashCode()
    {
        return Time.GetHashCode();
    }
}

/// <summary>
/// Propagates a partition key to outgoing envelopes using the following priority:
/// 1. The outgoing message's own GroupId (set by ByPropertyNamed / UseInferredMessageGrouping)
/// 2. The originator's PartitionKey (the actual Kafka message key of the incoming message)
/// 3. The originator's GroupId as a last resort
/// This is useful when using Kafka and you want cascaded messages to inherit the partition key
/// from the originating message's group/stream id without manually specifying DeliveryOptions.
/// </summary>
internal class GroupIdToPartitionKeyRule : IEnvelopeRule
{
    public void Modify(Envelope envelope)
    {
        // When published outside a handler context, promote the message's own GroupId
        // (set by ByPropertyNamed / UseInferredMessageGrouping) to the PartitionKey.
        if (envelope.PartitionKey.IsEmpty() && envelope.GroupId.IsNotEmpty())
        {
            envelope.PartitionKey = envelope.GroupId;
        }
    }

    public void ApplyCorrelation(IMessageContext originator, Envelope outgoing)
    {
        if (outgoing.PartitionKey.IsNotEmpty()) return;

        // Prefer the outgoing message's own GroupId — this is set by ByPropertyNamed or
        // UseInferredMessageGrouping and reflects the actual business partition key.
        if (outgoing.GroupId.IsNotEmpty())
        {
            outgoing.PartitionKey = outgoing.GroupId;
            return;
        }

        // Fall back to the originator's PartitionKey (the Kafka message key on the incoming
        // message), then to GroupId as a last resort.
        var key = originator.Envelope?.PartitionKey ?? originator.Envelope?.GroupId;
        if (key.IsNotEmpty())
        {
            outgoing.PartitionKey = key;
        }
    }
}

internal class PropagateHeadersRule : IEnvelopeRule
{
    private readonly string[] _headerNames;

    public PropagateHeadersRule(string[] headerNames)
    {
        _headerNames = headerNames;
    }

    // No incoming context available outside a handler — nothing to propagate
    public void Modify(Envelope envelope) { }

    public void ApplyCorrelation(IMessageContext originator, Envelope outgoing)
    {
        var incoming = originator.Envelope;
        if (incoming is null) return;

        foreach (var name in _headerNames)
        {
            if (incoming.Headers.TryGetValue(name, out var value))
            {
                outgoing.Headers[name] = value;
            }
        }
    }
}

/// <summary>
/// Propagates a single named header from the incoming envelope to the outgoing envelope
/// if it exists. This is a convenience rule for the common case of forwarding just one header.
/// </summary>
internal class PropagateOneHeaderRule : IEnvelopeRule
{
    private readonly string _headerName;

    public PropagateOneHeaderRule(string headerName)
    {
        _headerName = headerName;
    }

    // No incoming context available outside a handler — nothing to propagate
    public void Modify(Envelope envelope) { }

    public void ApplyCorrelation(IMessageContext originator, Envelope outgoing)
    {
        var incoming = originator.Envelope;
        if (incoming is null) return;

        if (incoming.Headers.TryGetValue(_headerName, out var value))
        {
            outgoing.Headers[_headerName] = value;
        }
    }
}

internal class LambdaEnvelopeRule : IEnvelopeRule
{
    private readonly Action<Envelope> _configure;

    public LambdaEnvelopeRule(Action<Envelope> configure)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    public void Modify(Envelope envelope)
    {
        _configure(envelope);
    }
}

internal class LambdaEnvelopeRule<T> : IEnvelopeRule
{
    private readonly Action<Envelope> _configure;

    public LambdaEnvelopeRule(Action<Envelope> configure)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    public void Modify(Envelope envelope)
    {
        if (envelope.Message is T)
        {
            _configure(envelope);
        }
    }
}