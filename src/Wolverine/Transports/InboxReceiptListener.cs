using Wolverine.Runtime;

namespace Wolverine.Transports;

/// <summary>
///     GH-5278. Stands in for a push transport's listener while a durable receiver takes in a batch the transport
///     has not answered yet. The durable receiver settles every envelope it receives: CompleteAsync once the envelope
///     is in the inbox (or was a duplicate, expired, or dead lettered), DeferAsync when it could not store it and wants
///     the transport to deliver it again. A broker redelivers a deferred message; a push transport like TCP cannot, so
///     only the sender can -- and only if the batch is not answered with success. Every settlement after the first one
///     belongs to the handling of the message, not its receipt, and goes to the real listener.
/// </summary>
internal class InboxReceiptListener : IListener
{
    private readonly IListener _inner;

    private readonly Dictionary<Envelope, TaskCompletionSource<bool>> _settled =
        new(ReferenceEqualityComparer.Instance);

    public InboxReceiptListener(IListener inner, IEnumerable<Envelope> envelopes)
    {
        _inner = inner;
        foreach (var envelope in envelopes)
        {
            _settled[envelope] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public IHandlerPipeline? Pipeline => _inner.Pipeline;

    public Uri Address => _inner.Address;

    /// <summary>
    ///     True once every envelope in the batch is settled and none of them was deferred. False if any was deferred,
    ///     or if they are not all settled before the timeout
    /// </summary>
    public async Task<bool> AllStoredAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        try
        {
            var results = await Task.WhenAll(_settled.Values.Select(x => x.Task)).WaitAsync(timeout, cancellation);
            return results.All(x => x);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return settle(envelope, true) ? ValueTask.CompletedTask : _inner.CompleteAsync(envelope);
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return settle(envelope, false) ? ValueTask.CompletedTask : _inner.DeferAsync(envelope);
    }

    public Task<bool> TryRequeueAsync(Envelope envelope) => _inner.TryRequeueAsync(envelope);

    // True if this was the receipt of the envelope, rather than a later settlement from handling it
    private bool settle(Envelope envelope, bool stored)
    {
        return _settled.TryGetValue(envelope, out var source) && source.TrySetResult(stored);
    }

    public ValueTask StopAsync() => _inner.StopAsync();

    // The real listener outlives every batch it receives, so a receipt never disposes it
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
