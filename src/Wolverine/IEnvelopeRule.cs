using System;

namespace Wolverine;

/// <summary>
/// Intercepts outgoing messages and alters the parameters or
/// metadata of the message before sending to the outgoing brokers
/// </summary>
public interface IEnvelopeRule
{
    void Modify(Envelope envelope);
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
