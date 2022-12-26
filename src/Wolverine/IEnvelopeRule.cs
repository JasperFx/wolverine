namespace Wolverine;

/// <summary>
///     Intercepts outgoing messages and alters the parameters or
///     metadata of the message before sending to the outgoing brokers
/// </summary>
public interface IEnvelopeRule
{
    void Modify(Envelope envelope);
}

internal class DeliverWithinRule : IEnvelopeRule
{
    public TimeSpan Time { get; }

    public DeliverWithinRule(TimeSpan time)
    {
        Time = time;
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

        if (obj.GetType() != this.GetType())
        {
            return false;
        }

        return Equals((DeliverWithinRule)obj);
    }

    public override int GetHashCode()
    {
        return Time.GetHashCode();
    }

    public void Modify(Envelope envelope)
    {
        envelope.DeliverWithin = Time;
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