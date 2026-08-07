using System.Collections.Concurrent;

namespace Wolverine.Runtime;

/// <summary>
/// Node-lifetime latch shared by every causation-reporting path so that each unique causation edge
/// reaches <see cref="IWolverineObserver.MessageCausedBy"/> exactly once per process.
///
/// The causation edge set is topology, not telemetry: it only grows when a code path first executes,
/// so re-reporting an edge that has already been observed tells an observer nothing new while costing
/// it real work. <see cref="Wolverine.Runtime.Handlers.MessageHandler"/> has always latched; the
/// endpoint-origin path (<see cref="EndpointCausation"/>, used by HTTP and gRPC endpoints) did not,
/// so a service that published one message per request re-reported the same edge on every request
/// forever. See GH-3869.
///
/// Both paths share one store deliberately -- an edge first seen through a message handler should not
/// be re-reported when the endpoint path sees the same triple. The key intentionally omits the
/// endpoint Uri: <c>MessageHandler</c> has never included it in its own key, and the endpoint path
/// always passes null for it, so the identity is the (cause, effect, handler) triple in both cases.
/// </summary>
internal static class CausationLatch
{
    private static readonly ConcurrentDictionary<string, byte> _known = new();

    /// <summary>
    /// True the first time this exact causation edge is seen in this process, false every time after.
    /// </summary>
    /// <param name="cause">The incoming message type name, or the endpoint origin for endpoint-originated publishes</param>
    /// <param name="effect">The outgoing message type name</param>
    /// <param name="handlerType">The handler type name, or the endpoint origin when there is no handler type</param>
    internal static bool ShouldReport(string cause, string effect, string handlerType)
    {
        return _known.TryAdd($"{cause}->{effect}@{handlerType}", 0);
    }

    /// <summary>
    /// Testing hook only. The latch is static for the life of the process, so tests that assert on
    /// report counts have to start from a known state.
    /// </summary>
    internal static void Clear()
    {
        _known.Clear();
    }
}
