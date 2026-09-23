using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    // GH-3959: the node's own load sampler, resolved lazily so the setting can be assigned any time
    // before the runtime starts. Null result = not advertising.
    //
    // There is deliberately no fallback monitor when none is configured: the runtime refuses to start
    // in that combination (WolverineRuntime.HostService.StartAsync), because a node advertising
    // nothing is treated as having unlimited headroom and silently becomes the cluster's preferred
    // placement target. A null here therefore means the feature is off, not that a default is missing.
    private INodeLoadMonitor? _loadMonitor;
    private bool _loadSamplingFailed;

    private double? sampleLoad()
    {
        if (!_runtime.Options.Durability.CapacityAwareAssignment)
        {
            return null;
        }

        _loadMonitor ??= _runtime.Options.Durability.NodeLoadMonitor;
        if (_loadMonitor == null)
        {
            return null;
        }

        try
        {
            var load = _loadMonitor.CurrentLoad();
            _loadSamplingFailed = false;
            return load;
        }
        catch (Exception e)
        {
            // Advertising no load is not neutral under capacity-aware assignment: the leader treats
            // this node as always having headroom AND orders it into the lowest load band for new
            // placements, so a persistently broken sampler makes this node the cluster's preferred
            // placement target. Warn once per outage (heartbeats would repeat it every second or so),
            // then stay quiet until sampling recovers.
            if (!_loadSamplingFailed)
            {
                _loadSamplingFailed = true;
                _logger.LogWarning(e,
                    "Error sampling node load; advertising no load until sampling recovers, so the leader will treat this node as having unlimited headroom for new agent placements");
            }
            else
            {
                _logger.LogDebug(e, "Error sampling node load; advertising no load this heartbeat");
            }

            return null;
        }
    }
}
