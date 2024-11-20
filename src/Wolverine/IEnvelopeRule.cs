using Wolverine.Util;

namespace Wolverine;

/// <summary>
///     Intercepts outgoing messages and alters the parameters or
///     metadata of the message before sending to the outgoing brokers
/// </summary>
public interface IEnvelopeRule
{
    void Modify(Envelope envelope);
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