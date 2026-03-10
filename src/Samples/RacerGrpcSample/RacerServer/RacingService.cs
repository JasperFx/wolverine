using System.Collections.Concurrent;
using ProtoBuf.Grpc;
using RacerContracts;
using Wolverine.Http.Grpc;

namespace RacerServer;

/// <summary>
/// Bidirectional-streaming gRPC endpoint demonstrating high-throughput real-time updates.
/// Tracks racer speeds in memory and yields updated standings as clients stream position updates.
/// Each gRPC call gets its own transient service instance with isolated state.
/// </summary>
[WolverineGrpcService]
public class RacingService : WolverineGrpcEndpointBase, IRacingService
{
    // Per-connection speed tracking (transient scope means this is isolated per call).
    private readonly ConcurrentDictionary<string, double> _speeds = new();

    public async IAsyncEnumerable<RacePosition> RaceAsync(
        IAsyncEnumerable<RacerUpdate> updates,
        CallContext context = default)
    {
        await foreach (var update in updates.WithCancellation(context.CancellationToken))
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
            if (position is not null) yield return position;
        }
    }
}
