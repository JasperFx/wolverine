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

/// <summary>
/// GH-4188. A receiver that delegates to another receiver rather than executing messages itself.
/// <see cref="ListeningAgent.EnqueueDirectlyAsync"/> and <see cref="ListeningAgent.LatchReceiver"/> both have to
/// reason about the *real* receiver -- which of NativeAck, Inline, Buffered or Durable it is -- and a wrapper
/// hides that. Worse than hiding it, <see cref="ReceiverWithRules"/> is itself an <see cref="ILocalQueue"/>, so
/// a wrapped NativeAck or Inline receiver actively matched the wrong branch. Wrappers can nest
/// (a GlobalPartitionedInterceptor over a ReceiverWithRules), so unwrap with <see cref="ReceiverExtensions.Unwrap"/>
/// rather than a single cast.
/// </summary>
internal interface IReceiverWrapper : IReceiver
{
    IReceiver Inner { get; }
}

internal static class ReceiverExtensions
{
    /// <summary>
    /// GH-4188. Peel every pass-through wrapper off a receiver to reach the one that actually executes messages.
    /// Use this for *deciding* what kind of receiver you have; dispatch still belongs on the outer receiver
    /// wherever the wrappers' own behavior -- applying incoming envelope rules, global-partition re-routing --
    /// has to happen.
    /// </summary>
    internal static IReceiver Unwrap(this IReceiver receiver)
    {
        while (receiver is IReceiverWrapper wrapper)
        {
            receiver = wrapper.Inner;
        }

        return receiver;
    }
}

internal class ReceiverWithRules : IReceiver, ILocalQueue, IReceiverWrapper
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

    // GH-4186: IHasQueueDepth rather than ILocalQueue, so a wrapped InlineReceiver or NativeAckReceiver -- neither
    // of which is a local queue -- still reports its real depth instead of a constant zero.
    public int QueueCount => Inner is IHasQueueDepth q ? q.QueueCount : 0;

    public DateTimeOffset? LastReceivedAt => (Inner as IHasQueueDepth)?.LastReceivedAt;

    public Uri Uri => Inner is ILocalQueue q ? q.Uri : new Uri("none://none");
}