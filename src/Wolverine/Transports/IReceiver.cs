namespace Wolverine.Transports;

public interface IReceiver : IDisposable
{
    ValueTask ReceivedAsync(IListener listener, Envelope[] messages);
    ValueTask ReceivedAsync(IListener listener, Envelope envelope);

    ValueTask DrainAsync();
}

internal class ReceiverWithRules : IReceiver
{
    private readonly IReceiver _inner;
    private readonly IEnvelopeRule[] _rules;

    public ReceiverWithRules(IReceiver inner, IEnumerable<IEnvelopeRule> rules)
    {
        _inner = inner;
        _rules = rules.ToArray();
    }

    public void Dispose()
    {
        _inner.Dispose();
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        foreach (var envelope in messages)
        {
            foreach (var rule in _rules)
            {
                rule.Modify(envelope);
            }
        }

        return _inner.ReceivedAsync(listener, messages);
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        foreach (var rule in _rules)
        {
            rule.Modify(envelope);
        }

        return _inner.ReceivedAsync(listener, envelope);
    }

    public async ValueTask DrainAsync()
    {
        throw new NotImplementedException();
    }
}