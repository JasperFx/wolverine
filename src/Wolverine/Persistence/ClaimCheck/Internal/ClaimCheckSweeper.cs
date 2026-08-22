using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wolverine.Persistence.ClaimCheck.Internal;

/// <summary>
/// Periodically deletes off-loaded claim-check payloads older than the configured time to live from every
/// backend that implements <see cref="IClaimCheckStoreWithExpiration"/>. Registered only when
/// <see cref="ClaimCheckConfiguration.DeletePayloadsOlderThan"/> was called, so the default behavior is
/// unchanged. See GH-3509.
/// </summary>
/// <remarks>
/// This runs on <b>every</b> node rather than being pinned to the cluster leader. Agent families — the usual
/// home for cluster-singleton work — are only instantiated in <see cref="DurabilityMode.Balanced"/>, which
/// requires message persistence and a node table. Claim checks have no such requirement: UseClaimCheck only
/// decorates the default serializer, so a perfectly ordinary app can use claim checks with no message store
/// at all, and a leader-pinned sweeper would silently never run for those users. Deleting by age is
/// idempotent and safe to run concurrently, so every-node execution costs nothing but a bounded delete per
/// interval, and each node jitters its own schedule so a large cluster does not stampede the store.
/// </remarks>
internal sealed class ClaimCheckSweeper : BackgroundService
{
    // Bounds how many back-to-back full batches one wake-up will drain before yielding to the interval
    // again. Without this, a store holding millions of expired payloads would keep a first sweep running
    // indefinitely and delay a graceful shutdown.
    private const int MaxConsecutivePasses = 20;

    private readonly ClaimCheckSweepSettings _settings;
    private readonly ILogger _logger;
    private readonly Random _jitter = new();

    private readonly HashSet<Type> _warnedUnsupported = new();

    public ClaimCheckSweeper(ClaimCheckSweepSettings settings, ILogger<ClaimCheckSweeper> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(nextDelay(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var store in _settings.Router.AllStores())
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                await sweepAsync(store, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Spread each node's sweep across the interval so a rolling deployment does not leave every node
    /// hitting the store on the same cadence. Jitter is +/- 20% of the configured interval.
    /// </summary>
    private TimeSpan nextDelay()
    {
        var jitterRange = _settings.Interval.TotalMilliseconds * 0.2;
        var offset = (_jitter.NextDouble() * 2 - 1) * jitterRange;
        return TimeSpan.FromMilliseconds(Math.Max(1000, _settings.Interval.TotalMilliseconds + offset));
    }

    private async Task sweepAsync(IClaimCheckStore store, CancellationToken stoppingToken)
    {
        if (!tryResolveExpiringStore(store, out var expiring))
        {
            return;
        }

        var total = 0;

        for (var pass = 0; pass < MaxConsecutivePasses; pass++)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            int deleted;
            try
            {
                var cutoff = DateTimeOffset.UtcNow - _settings.TimeToLive;
                deleted = await expiring.DeleteExpiredPayloadsAsync(cutoff, _settings.BatchSize, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // A sweep failure is never fatal — the payloads are still there and the next pass will
                // retry. Log and move on rather than tearing down the host's background service.
                _logger.LogError(e,
                    "Failed to sweep expired claim-check payloads from {Store}. The sweep will be retried in {Interval}.",
                    expiring.GetType().FullName, _settings.Interval);
                break;
            }

            total += deleted;

            // A short batch means the backlog is drained; wait out the interval before looking again.
            if (deleted < _settings.BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            _logger.LogInformation(
                "Deleted {Count} claim-check payload(s) older than {TimeToLive} from {Store}.",
                total, _settings.TimeToLive, expiring.GetType().FullName);
        }
    }

    /// <summary>
    /// Unwrap the deferred DI proxy (GH-3564) before testing for expiration support, and warn exactly once
    /// per backend type that cannot be swept so the operator knows to configure a native lifecycle rule
    /// instead of assuming the TTL they set is doing something.
    /// </summary>
    private bool tryResolveExpiringStore(IClaimCheckStore store, out IClaimCheckStoreWithExpiration expiring)
    {
        expiring = null!;

        var target = store;
        if (store is DeferredClaimCheckStore deferred)
        {
            var resolved = deferred.TryResolve();
            if (resolved is null)
            {
                // The container is not bound yet; try again on the next pass rather than warning.
                return false;
            }

            target = resolved;
        }

        if (target is IClaimCheckStoreWithExpiration supported)
        {
            expiring = supported;
            return true;
        }

        if (_warnedUnsupported.Add(target.GetType()))
        {
            _logger.LogWarning(
                "Claim-check payload expiration is configured, but the {Store} backend does not support " +
                "Wolverine-driven sweeping and will be skipped. Object stores such as Azure Blob Storage, " +
                "Amazon S3, and Google Cloud Storage expire objects far more cheaply through their own " +
                "server-side lifecycle rules — configure one against the claim-check prefix instead. " +
                "See https://wolverinefx.io/guide/durability/claim-checks.html",
                target.GetType().FullName);
        }

        return false;
    }
}
