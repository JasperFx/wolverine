using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

/// <summary>
///     GH-5276. Stands in for a broker channel while a durable local queue receives a batch posted to the HTTP
///     transport. The durable receiver settles every envelope it receives: CompleteAsync once the envelope is in
///     the inbox (or was a duplicate, expired, or dead lettered), DeferAsync when it could not store it and wants
///     the transport to deliver it again. A broker redelivers a deferred message; over HTTP only the sender can,
///     and only if the batch is not answered with success
/// </summary>
internal class BatchReceiptListener : IListener
{
    private readonly Dictionary<Envelope, TaskCompletionSource<bool>> _settled =
        new(ReferenceEqualityComparer.Instance);

    public BatchReceiptListener(Uri address, IEnumerable<Envelope> envelopes)
    {
        Address = address;
        foreach (var envelope in envelopes)
        {
            _settled[envelope] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public IHandlerPipeline? Pipeline => null;

    public Uri Address { get; }

    /// <summary>
    ///     True once every envelope in the batch is settled and none of them was deferred
    /// </summary>
    public async Task<bool> AllStoredAsync(CancellationToken cancellation)
    {
        var results = await Task.WhenAll(_settled.Values.Select(x => x.Task)).WaitAsync(cancellation);
        return results.All(x => x);
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        settle(envelope, true);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        settle(envelope, false);
        return ValueTask.CompletedTask;
    }

    // The first settlement is the answer. Anything after it belongs to the handling of the message, not its receipt
    private void settle(Envelope envelope, bool stored)
    {
        if (_settled.TryGetValue(envelope, out var source))
        {
            source.TrySetResult(stored);
        }
    }

    public ValueTask StopAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
