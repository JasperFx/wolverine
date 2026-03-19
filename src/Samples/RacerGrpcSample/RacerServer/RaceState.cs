using System.Collections.Concurrent;

namespace RacerServer;

/// <summary>
/// Singleton service that maintains the current race state across all racer updates.
///
/// NOTE: This is a demo-specific implementation used to aggregate streaming updates into a
/// materialized view for console display. In production scenarios, you would typically handle
/// stream aggregation on the client side or use a proper stream processing/read model pattern.
/// This approach differs from canonical gRPC streaming examples where each response is independent.
/// </summary>
public class RaceState
{
    private readonly ConcurrentDictionary<string, double> _speeds = new();

    public void UpdateSpeed(string racerId, double speed)
    {
        _speeds[racerId] = speed;
    }

    public IReadOnlyDictionary<string, double> GetCurrentSpeeds()
    {
        return _speeds;
    }
}
