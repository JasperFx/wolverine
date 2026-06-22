using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.Runtime.Heartbeat;

/// <summary>
/// Lightweight, store-independent liveness for a Solo host that has no durable message store.
/// A storeless Solo node never runs the heavyweight <c>NodeAgentController</c> path
/// (<c>StartSoloModeAsync</c> sits behind the <see cref="NullMessageStore"/> early return), so
/// without this it would never emit the <c>NodeStarted()</c>/<c>NodeStopped()</c> lifecycle
/// bookends and would heartbeat under an unstable, per-process node id. See #3188.
///
/// The stable node number itself is assigned by the runtime <em>before</em> the messaging
/// transports start (envelope ownership and the heartbeat both read it); this service only owns
/// the lifecycle bookends. It is registered <em>after</em> the runtime hosted service so
/// <c>NodeStarted()</c> fires once transports are up, and — because hosted services stop in
/// reverse order — <c>NodeStopped()</c> fires before the runtime tears the transports down, so a
/// publish-based observer (e.g. CritterWatch) can still emit the final record.
/// </summary>
internal class SoloHeartbeatService : IHostedService
{
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<SoloHeartbeatService> _logger;
    private bool _started;

    public SoloHeartbeatService(IWolverineRuntime runtime, ILogger<SoloHeartbeatService> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    // Only a Solo host with no durable store needs this — every other shape already gets its
    // node identity and lifecycle through the NodeAgentController path.
    private bool ShouldRun => _runtime.Options.Durability.Mode == DurabilityMode.Solo
                              && _runtime.Storage is NullMessageStore;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ShouldRun)
        {
            return;
        }

        _started = true;
        await _runtime.Observer.NodeStarted();

        _logger.LogInformation("Solo node {NodeNumber} started without a durable message store",
            _runtime.Options.Durability.AssignedNodeNumber);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        await _runtime.Observer.NodeStopped();
    }
}
