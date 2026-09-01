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

    /// <summary>
    /// GH-4215. Optional classification of a failure as "the broker entity this loop reads from does not exist".
    /// Null by default, which keeps the pre-existing behaviour: every failure is a transient one worth retrying
    /// at the same cadence.
    ///
    /// <para>
    /// A missing entity is not transient in the way a socket blip is. Retrying the receive cannot bring the
    /// queue back, so before this the loop retried once a second forever -- ~23 error lines per second across a
    /// fleet, 180MB of stdout in 45 minutes, and a listener that never consumed again until the host restarted.
    /// </para>
    /// </summary>
    public Func<Exception, bool>? IsEntityMissing { get; init; }

    /// <summary>
    /// GH-4215. Optional re-declare step, run when <see cref="IsEntityMissing"/> classifies a failure. This is
    /// what actually heals the loop: the application already knows how to declare the entity, because
    /// AutoProvision declared it at startup -- nothing simply re-ran that after startup. Transports should wire
    /// this to the endpoint's own <c>SetupAsync</c>, and only when the endpoint was auto-provisioned: re-creating
    /// an entity the application did not create is not Wolverine's call to make.
    /// </summary>
    public Func<CancellationToken, Task>? RedeclareAsync { get; init; }

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

                    // GH-4215: a successful iteration clears an EntityMissing streak, so a loop that healed
                    // reports Running again rather than staying stuck on the diagnosis that got it there.
                    if (_status == ReceiveLoopStatus.EntityMissing)
                    {
                        _status = ReceiveLoopStatus.Running;
                        _logger.LogInformation("The receive loop for {Uri} is consuming again", _uri);
                    }

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
                    var entityMissing = IsEntityMissing?.Invoke(e) ?? false;

                    if (entityMissing)
                    {
                        _status = ReceiveLoopStatus.EntityMissing;

                        // GH-4215: logged on the first failure of a streak and then every 60th, rather than every
                        // iteration. The old behaviour drowned out everything else the host logged, which is its
                        // own outage on top of the one being reported.
                        if (failures == 1 || failures % 60 == 0)
                        {
                            _logger.LogWarning(e,
                                "The broker entity for {Uri} does not exist (consecutive failures: {Count}). Retrying the receive cannot succeed until it is re-declared.",
                                _uri, failures);
                        }

                        await tryRedeclareAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogError(e,
                            "Error in the receive loop for {Uri} (consecutive failures: {Count}); backing off and retrying",
                            _uri, failures);
                    }

                    try
                    {
                        await Task.Delay(backoffFor(failures, entityMissing), _cancellation.Token)
                            .ConfigureAwait(false);
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
    // GH-4215: a missing entity gets a far longer floor than a transient blip. Retrying the receive cannot
    // succeed until something re-declares, so a one-second cadence buys nothing and costs a log storm.
    private static TimeSpan backoffFor(int failures, bool entityMissing)
    {
        if (entityMissing) return 5.Seconds();

        return failures > 5 ? 1.Seconds() : (failures * 100).Milliseconds();
    }

    private async Task tryRedeclareAsync()
    {
        if (RedeclareAsync == null) return;

        try
        {
            await RedeclareAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception redeclare)
        {
            // Never let the healing attempt take the loop down -- it is best-effort by nature, and the broker
            // being unreachable is exactly when it will fail.
            _logger.LogWarning(redeclare, "Could not re-declare the broker entity for {Uri}", _uri);
        }
    }

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
