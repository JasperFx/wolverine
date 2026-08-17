using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Wolverine.SignalR.Internals;

/// <summary>
///     GH-3972. Accumulates outgoing SignalR messages per destination and flushes each destination's backlog as
///     one <see cref="CoalescedSignalRMessage" />.
/// </summary>
/// <remarks>
///     <para>
///         This is a <b>sender-side</b> buffer, which is the whole point of the issue. Getting the same effect
///         by routing outbound messages through a local queue is what produced a three-issue bug family: a
///         handler forwarding with <c>SendAsync</c> re-sent onto the very queue it was read from, and the queue
///         filled until back pressure blocked the producer. Nothing round-trips a queue here, so there is no
///         queue to re-enter. It also sits after the outbox, unlike an application-level accumulator.
///     </para>
///     <para>
///         Buffers are keyed by <b>destination and operation</b>. An application that only ever broadcasts
///         would get away with one global buffer; the transport must not, because coalescing a message bound
///         for connection A together with one bound for connection B delivers each of them to both.
///     </para>
/// </remarks>
internal class OutgoingCoalescer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DestinationBuffer> _buffers = new();
    private readonly OutgoingCoalescingOptions _options;
    private readonly IHubContext<Hub> _hubContext;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger? _logger;
    private volatile bool _disposed;

    public OutgoingCoalescer(OutgoingCoalescingOptions options, IHubContext<Hub> hubContext,
        JsonSerializerOptions jsonOptions, ILogger? logger)
    {
        _options = options;
        _hubContext = hubContext;
        _jsonOptions = jsonOptions;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(WebSocketRouting.ILocator locator, string operation, string json)
    {
        if (_disposed)
        {
            // Racing shutdown -- send it directly rather than dropping it into a buffer that will never
            // be flushed
            return new ValueTask(locator.Find(_hubContext).SendAsync(operation, json));
        }

        var key = $"{operation}|{locator}";
        var buffer = _buffers.GetOrAdd(key, _ => new DestinationBuffer(locator, operation, this));

        return buffer.EnqueueAsync(json);
    }

    /// <summary>
    ///     Flush every destination. Called on shutdown so messages enqueued just before stop are not dropped.
    /// </summary>
    public async Task DrainAsync()
    {
        foreach (var buffer in _buffers.Values)
        {
            await buffer.FlushAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // Drain BEFORE latching, or anything already buffered is lost -- the drain-on-shutdown the issue
        // asks for is the entire reason a sender-side buffer is safe to use for delivery.
        await DrainAsync();

        _disposed = true;

        foreach (var buffer in _buffers.Values)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
    }

    private async Task SendCoalescedAsync(WebSocketRouting.ILocator locator, string operation,
        IReadOnlyList<string> items)
    {
        try
        {
            if (items.Count == 1)
            {
                // A "batch" of one is just the message. Sending it on the normal operation keeps a client
                // that has not opted into coalescing working for the common trickle case.
                await locator.Find(_hubContext).SendAsync(operation, items[0]);
                return;
            }

            var json = CoalescedSignalRMessage.ToJson(items, _jsonOptions);

            if (_logger?.IsEnabled(LogLevel.Debug) ?? false)
            {
                _logger.LogDebug("Sent a coalesced batch of {Count} messages via SignalR to {Destination}",
                    items.Count, locator);
            }

            await locator.Find(_hubContext).SendAsync(SignalRTransport.CoalescedOperation, json);
        }
        catch (Exception e)
        {
            _logger?.LogError(e,
                "Error while sending a coalesced batch of {Count} messages via SignalR to {Destination}",
                items.Count, locator);
        }
    }

    /// <summary>
    ///     One destination's backlog. The timer is started by the first message of a window rather than run
    ///     continuously, so an idle destination costs nothing.
    /// </summary>
    private class DestinationBuffer : IDisposable
    {
        private readonly WebSocketRouting.ILocator _locator;
        private readonly string _operation;
        private readonly OutgoingCoalescer _parent;
        private readonly List<string> _pending = [];
        private readonly SemaphoreSlim _lock = new(1, 1);
        private CancellationTokenSource? _timer;

        public DestinationBuffer(WebSocketRouting.ILocator locator, string operation, OutgoingCoalescer parent)
        {
            _locator = locator;
            _operation = operation;
            _parent = parent;
        }

        public async ValueTask EnqueueAsync(string json)
        {
            List<string>? toSend = null;

            await _lock.WaitAsync();
            try
            {
                _pending.Add(json);

                if (_pending.Count >= _parent._options.MaxBatchSize)
                {
                    toSend = drainLocked();
                }
                else if (_timer == null)
                {
                    startTimerLocked();
                }
            }
            finally
            {
                _lock.Release();
            }

            if (toSend != null)
            {
                await _parent.SendCoalescedAsync(_locator, _operation, toSend);
            }
        }

        public async Task FlushAsync()
        {
            List<string>? toSend;

            await _lock.WaitAsync();
            try
            {
                toSend = drainLocked();
            }
            finally
            {
                _lock.Release();
            }

            if (toSend != null)
            {
                await _parent.SendCoalescedAsync(_locator, _operation, toSend);
            }
        }

        private List<string>? drainLocked()
        {
            _timer?.Cancel();
            _timer?.Dispose();
            _timer = null;

            if (_pending.Count == 0) return null;

            // Arrival order, which is what the issue specifies for ordering within a coalesced envelope
            var copy = new List<string>(_pending);
            _pending.Clear();
            return copy;
        }

        private void startTimerLocked()
        {
            var cts = new CancellationTokenSource();
            _timer = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_parent._options.FlushInterval, cts.Token);
                    if (!cts.Token.IsCancellationRequested)
                    {
                        await FlushAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Flushed early by MaxBatchSize or by a drain
                }
                catch (Exception e)
                {
                    _parent._logger?.LogError(e, "Error while flushing a coalesced SignalR batch");
                }
            });
        }

        public void Dispose()
        {
            _timer?.Cancel();
            _timer?.Dispose();
            _lock.Dispose();
        }
    }
}
