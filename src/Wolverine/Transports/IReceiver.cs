using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Transports;

public interface IReceiver : IDisposable
{
    ValueTask ReceivedAsync(IListener listener, Envelope[] messages);
    ValueTask ReceivedAsync(IListener listener, Envelope envelope);

    ValueTask DrainAsync();
    
    IHandlerPipeline Pipeline { get; }
}

internal class ReceiverWithRules : IReceiver, ILocalQueue
{
    public ReceiverWithRules(IReceiver inner, IEnumerable<IEnvelopeRule> rules)
    {
        Inner = inner;
        Rules = rules.ToArray();
    }

    public IHandlerPipeline Pipeline => Inner.Pipeline;

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

    public void Enqueue(Envelope envelope)
    {
        if (Inner is ILocalQueue queue)
        {
            queue.Enqueue(envelope);
        }
        else
        {
            throw new InvalidOperationException("There is no active, local queue for this listening endpoint at " +
                                                envelope.Destination);
        }
    }
    
    public ValueTask EnqueueAsync(Envelope envelope)
    {
        if (Inner is ILocalQueue queue)
        {
            return queue.EnqueueAsync(envelope);
        }

        throw new InvalidOperationException("There is no active, local queue for this listening endpoint at " +
                                            envelope.Destination);
    }

    public int QueueCount => Inner is ILocalQueue q ? q.QueueCount : 0;
    public Uri Uri => Inner is ILocalQueue q ? q.Uri : new Uri("none://none");
}