using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Heartbeat;

/// <summary>
/// Hosted service that publishes a <see cref="WolverineHeartbeat"/> on a regular cadence
/// dictated by <see cref="WolverineOptions.Heartbeat"/>. Registered through
/// <see cref="WolverineOptionsExtensions.EnableHeartbeats"/>.
/// </summary>
/// <remarks>
/// The service obtains its publish path from a freshly-constructed <see cref="MessageBus"/>
/// over the supplied <see cref="IWolverineRuntime"/>, so heartbeats traverse the normal
/// Wolverine routing pipeline. If <see cref="HeartbeatPolicy.Enabled"/> is <c>false</c>,
/// <see cref="ExecuteAsync"/> returns immediately without scheduling any work.
/// </remarks>
public class HeartbeatBackgroundService : BackgroundService
{
    private readonly IWolverineRuntime _runtime;
    private readonly DateTimeOffset _startedAt;

    public HeartbeatBackgroundService(IWolverineRuntime runtime)
    {
        _runtime = runtime;
        _startedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// For tests: time source override, defaults to <c>DateTimeOffset.UtcNow</c>.
    /// </summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policy = _runtime.Options.Heartbeat;
        if (!policy.Enabled)
        {
            return;
        }

        var bus = new MessageBus(_runtime);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(policy.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (stoppingToken.IsCancellationRequested) return;

            try
            {
                var now = Now();
                var heartbeat = new WolverineHeartbeat(
                    _runtime.Options.ServiceName ?? string.Empty,
                    _runtime.Options.Durability.AssignedNodeNumber,
                    now,
                    now - _startedAt);

                await bus.PublishAsync(heartbeat).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Heartbeat failure must never crash the host. Log and continue.
                _runtime.Logger.LogWarning(e, "Failed to publish WolverineHeartbeat");
            }
        }
    }
}
