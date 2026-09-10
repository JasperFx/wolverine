using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    // GH-3959: the node's own load sampler, created lazily so the setting can be assigned any time
    // before the runtime starts. Null result = not advertising.
    private INodeLoadMonitor? _loadMonitor;

    private double? sampleLoad()
    {
        if (!_runtime.Options.Durability.CapacityAwareAssignment)
        {
            return null;
        }

        _loadMonitor ??= _runtime.Options.Durability.NodeLoadMonitor ?? new MemoryPressureLoadMonitor();

        try
        {
            return _loadMonitor.CurrentLoad();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error sampling node load; advertising no load this heartbeat");
            return null;
        }
    }
}
