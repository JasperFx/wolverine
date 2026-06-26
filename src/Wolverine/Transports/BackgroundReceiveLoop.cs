using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Transports;

/// <summary>
///     A shared, managed background receive loop for polling-style listeners (SQS, Kafka, Redis, the database queue
///     transports, …). It owns the loop <see cref="Task"/> and standardizes the behavior every hand-rolled listener
///     loop used to re-implement (divergently): cancellation, per-iteration try/catch → log → exponential backoff →
///     continue, an idle delay when an iteration finds no work, a heartbeat timestamp, fault detection, and safe
///     async teardown. The heartbeat + fault state are surfaced through <see cref="IReportReceiveLoopHealth"/> so an
///     external monitor can see a loop that has faulted or hung — the gap that connection state alone can't (GH-3236).
/// </summary>
public sealed class BackgroundReceiveLoop : IAsyncDisposable, IReportReceiveLoopHealth
{
    // One iteration of the loop. Returns true when it did work (received/processed messages), false when idle —
    // an idle iteration is followed by IdleDelay so a quiet queue isn't hot-polled.
    private readonly Func<CancellationToken, Task<bool>> _iteration;
    private readonly TimeSpan _idleDelay;
    private readonly ILogger _logger;
    private readonly Uri _uri;
    private readonly CancellationTokenSource _cancellation;

    private Task? _task;
    private volatile int _consecutiveFailures;
    private long _lastActivityTicks;
    private volatile ReceiveLoopStatus _status = ReceiveLoopStatus.NotStarted;

    public BackgroundReceiveLoop(Uri uri, ILogger logger, Func<CancellationToken, Task<bool>> iteration,
        CancellationToken parentToken, TimeSpan? idleDelay = null)
    {
        _uri = uri ?? throw new ArgumentNullException(nameof(uri));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _iteration = iteration ?? throw new ArgumentNullException(nameof(iteration));
        _idleDelay = idleDelay ?? 250.Milliseconds();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
    }

    /// <summary>The linked cancellation token that ends the loop. Use it for the iteration's own awaits.</summary>
    public CancellationToken Token => _cancellation.Token;

    public int ConsecutiveFailures => _consecutiveFailures;

    public ReceiveLoopStatus ReceiveLoopStatus => _status;

    public DateTimeOffset? LastReceiveLoopActivityAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastActivityTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Start iterating on a background task. Idempotent — a second call is a no-op.</summary>
    public void Start()
    {
        if (_task != null)
        {
            return;
        }

        _status = ReceiveLoopStatus.Running;
        _task = Task.Run(runAsync, _cancellation.Token);
    }

    private async Task runAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                // Heartbeat: bump before each iteration so a hung iteration shows a stale timestamp.
                Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.Ticks);

                try
                {
                    var didWork = await _iteration(_cancellation.Token).ConfigureAwait(false);
                    _consecutiveFailures = 0;

                    if (!didWork)
                    {
                        await Task.Delay(_idleDelay, _cancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    var failures = Interlocked.Increment(ref _consecutiveFailures);
                    _logger.LogError(e,
                        "Error in the receive loop for {Uri} (consecutive failures: {Count}); backing off and retrying",
                        _uri, failures);

                    try
                    {
                        await Task.Delay(backoffFor(failures), _cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _status = ReceiveLoopStatus.Stopped;
        }
        catch (OperationCanceledException)
        {
            _status = ReceiveLoopStatus.Stopped;
        }
        catch (Exception e)
        {
            // The loop scaffolding itself fell over — this is the silent-death case GH-3236 exists to surface.
            _status = ReceiveLoopStatus.Faulted;
            _logger.LogError(e, "The receive loop for {Uri} terminated unexpectedly and is no longer consuming", _uri);
        }
    }

    // Mirrors the historical SQS backoff curve: a gentle ramp, capped at one second.
    private static TimeSpan backoffFor(int failures)
        => failures > 5 ? 1.Seconds() : (failures * 100).Milliseconds();

    /// <summary>
    /// Cancel the loop and await its completion, up to <paramref name="timeout"/>. Safe to call from background
    /// threads / shutdown; swallows the expected cancellation.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (!_cancellation.IsCancellationRequested)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_task == null)
        {
            return;
        }

        try
        {
            await _task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("Receive loop for {Uri} did not drain within {Timeout} during shutdown", _uri, timeout);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cancellation.IsCancellationRequested)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_task != null)
        {
            try
            {
                // We own _task (created in Start) — awaiting it here is safe.
#pragma warning disable VSTHRD003
                await _task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error awaiting the receive loop for {Uri} during disposal", _uri);
            }
        }

        _cancellation.Dispose();
    }
}
