using System.Collections.Concurrent;

namespace RacerServer;

/// <summary>
///     Singleton that tracks each racer's current speed so the handler can compute standings.
///     <para>
///         This is a demo-friendly materialized view — it aggregates individual stream items
///         into shared state that the handler reads on every update. A production design would
///         typically aggregate on the client side or via a dedicated read model.
///     </para>
/// </summary>
public class RaceState
{
    private readonly ConcurrentDictionary<string, double> _speeds = new();

    public void UpdateSpeed(string racerId, double speed) => _speeds[racerId] = speed;

    public IReadOnlyDictionary<string, double> GetCurrentSpeeds() => _speeds;
}
