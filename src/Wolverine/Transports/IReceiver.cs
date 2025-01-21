namespace Wolverine.Transports;

public interface IReceiver : IDisposable
{
    ValueTask ReceivedAsync(IListener listener, Envelope[] messages);
    ValueTask ReceivedAsync(IListener listener, Envelope envelope);

    ValueTask DrainAsync();
}

internal class ReceiverWithRules : IReceiver
{
    public ReceiverWithRules(IReceiver inner, IEnumerable<IEnvelopeRule> rules)
    {
        Inner = inner;
        Rules = rules.ToArray();
    }

    public IReceiver Inner { get; }

    public IEnvelopeRule[] Rules { get; }

    public void Dispose()
    {
        Inner.Dispose();
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        foreach (var envelope in messages)
        {
            foreach (var rule in Rules)
            {
                rule.Modify(envelope);
            }
        }

        return Inner.ReceivedAsync(listener, messages);
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        foreach (var rule in Rules)
        {
            rule.Modify(envelope);
        }

        return Inner.ReceivedAsync(listener, envelope);
    }

    public ValueTask DrainAsync()
    {
        return Inner.DrainAsync();
    }
}