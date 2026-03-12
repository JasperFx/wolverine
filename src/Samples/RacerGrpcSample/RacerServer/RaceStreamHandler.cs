using System.Collections.Concurrent;
using RacerContracts;

namespace RacerServer;

/// <summary>
/// Wolverine streaming handler that processes a stream of racer updates and yields updated standings.
/// Demonstrates IMessageBus.StreamAsync integration with IAsyncEnumerable return types.
/// This handler is invoked by the gRPC endpoint through the Wolverine pipeline.
/// </summary>
public class RaceStreamHandler
{
    // Per-request speed tracking (handler is transient scope, isolated per invocation)
    private readonly ConcurrentDictionary<string, double> _speeds = new();

    /// <summary>
    /// Wolverine streaming handler that processes racer updates and yields race positions.
    /// Returns IAsyncEnumerable to enable streaming through IMessageBus.StreamAsync.
    /// </summary>
    public async IAsyncEnumerable<RacePosition> Handle(RacerUpdate update)
    {
        _speeds[update.RacerId] = update.Speed;

        // Compute standings: highest speed = position 1.
        var standings = _speeds
            .OrderByDescending(kv => kv.Value)
            .Select((kv, idx) => new RacePosition
            {
                RacerId = kv.Key,
                Position = idx + 1,
                Speed = kv.Value
            })
            .ToList();

        // Yield the updated position for this racer.
        var position = standings.FirstOrDefault(p => p.RacerId == update.RacerId);
        if (position is not null)
        {
            yield return position;
        }

        // Simulate some async work (e.g., database lookup, external API call)
        await Task.Delay(1);
    }
}
