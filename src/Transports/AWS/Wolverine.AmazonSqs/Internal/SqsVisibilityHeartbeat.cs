using System.Collections.Concurrent;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;

namespace Wolverine.AmazonSqs.Internal;

/// <summary>
///     GH-4019. Keeps the SQS messages an <em>inline</em> listener is still working on invisible by
///     periodically extending their visibility timeout until they are settled. Wolverine sets the
///     visibility timeout once, on the receive, and an inline listener processes a received batch one
///     message at a time -- so the tenth message of a receive has been aging against that timeout
///     through nine handler executions before its own starts. Past the timeout SQS redelivers the
///     message while the handler is still running, the second copy executes too, and the first
///     copy's eventual delete carries a stale receipt handle that SQS accepts without deleting anything.
/// </summary>
/// <remarks>
///     Only meaningful for <see cref="Wolverine.Configuration.EndpointMode.Inline" />. Durable endpoints
///     delete the message right after the inbox insert and buffered endpoints delete on receipt, so
///     neither holds a message under the visibility timeout while a handler runs. The SQS call itself
///     is injected so the scheduling can be tested without a broker; the tick is a maximum age, not a
///     debounce, and nothing is sent on a tick when nothing is in flight -- a listener whose batches
///     finish inside half the visibility timeout never issues an extension at all.
/// </remarks>
internal class SqsVisibilityHeartbeat : IAsyncDisposable
{
    /// <summary>
    ///     Extend the visibility of these messages. Returns the messages that should no longer be
    ///     tracked, e.g. because SQS rejected their receipt handle (already deleted or redelivered).
    /// </summary>
    public delegate Task<IReadOnlyList<Message>> ExtendVisibility(Message[] messages, CancellationToken token);

    private readonly ConcurrentDictionary<string, Tracked> _inFlight = new();
    private readonly ExtendVisibility _extend;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _extension;
    private readonly TimeSpan _maximum;
    private readonly ILogger _logger;
    private readonly Uri _uri;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _loop;

    /// <param name="visibilityTimeout">The queue's visibility timeout; each extension re-arms the message for this long</param>
    /// <param name="maximum">Longest a single message is kept invisible from its receipt. SQS caps this at 12 hours</param>
    /// <param name="extend">The SQS call</param>
    /// <param name="uri">Endpoint Uri, for logging</param>
    /// <param name="logger"></param>
    /// <param name="cancellation">Runtime shutdown</param>
    /// <param name="interval">Tick interval. Defaults to half the visibility timeout, never under one second</param>
    public SqsVisibilityHeartbeat(TimeSpan visibilityTimeout, TimeSpan maximum, ExtendVisibility extend, Uri uri,
        ILogger logger, CancellationToken cancellation, TimeSpan? interval = null)
    {
        _extension = visibilityTimeout;
        _maximum = maximum;
        _extend = extend;
        _uri = uri;
        _logger = logger;

        var half = TimeSpan.FromTicks(visibilityTimeout.Ticks / 2);
        _interval = interval ?? (half < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : half);

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _loop = Task.Run(runAsync);
    }

    public TimeSpan Interval => _interval;

    /// <summary>
    ///     Number of messages currently being kept alive
    /// </summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>
    ///     Start keeping these received messages invisible until each is settled or untracked
    /// </summary>
    public void Track(IEnumerable<Message> messages)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var message in messages)
        {
            if (message.ReceiptHandle == null) continue;
            _inFlight.TryAdd(message.ReceiptHandle, new Tracked(message, now));
        }
    }

    /// <summary>
    ///     This message has been settled (deleted, or handed to a requeue/dead-letter path that
    ///     deletes it); stop extending it.
    /// </summary>
    public void Settled(Message message)
    {
        if (message.ReceiptHandle != null)
        {
            _inFlight.TryRemove(message.ReceiptHandle, out _);
        }
    }

    /// <summary>
    ///     Stop tracking every one of these messages, settled or not. Called when a received batch has
    ///     been fully processed -- anything still unsettled is in a retry block that will delete it.
    /// </summary>
    public void Untrack(IEnumerable<Message> messages)
    {
        foreach (var message in messages)
        {
            Settled(message);
        }
    }

    private async Task runAsync()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_inFlight.IsEmpty) continue;

            try
            {
                await TickAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Next tick tries again; an extension that never lands only means the message
                // reappears at its visibility timeout, which is exactly today's behavior.
                _logger.LogWarning(e, "Error extending the visibility of in-flight SQS messages at {Uri}", _uri);
            }
        }
    }

    /// <summary>
    ///     One pass: extend everything in flight that is still inside the maximum, drop what is not.
    ///     Exposed for tests.
    /// </summary>
    internal async Task TickAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var due = new List<Message>();

        foreach (var pair in _inFlight)
        {
            var tracked = pair.Value;

            // Every extension pushes the message's invisibility out by the full visibility timeout,
            // so stop before an extension would carry it past the maximum
            if (now - tracked.ReceivedAt + _extension > _maximum)
            {
                if (_inFlight.TryRemove(pair.Key, out _))
                {
                    _logger.LogWarning(
                        "Message {MessageId} at {Uri} has been in flight for {Elapsed} and will not be kept invisible past the maximum of {Maximum}; SQS may redeliver it while it is still being handled",
                        tracked.Message.MessageId, _uri, now - tracked.ReceivedAt, _maximum);
                }

                continue;
            }

            due.Add(tracked.Message);
        }

        if (due.Count == 0) return;

        var dropped = await _extend(due.ToArray(), token);
        foreach (var message in dropped)
        {
            Settled(message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            // The loop exits on the next tick at the latest; bound the wait so disposal of a
            // listener can never hang on it
            await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled or timed out -- either way the loop is done with as far as we care
        }

        _cancellation.Dispose();
    }

    private sealed record Tracked(Message Message, DateTimeOffset ReceivedAt);
}
