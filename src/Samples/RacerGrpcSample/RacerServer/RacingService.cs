using System.Collections.Concurrent;
using ProtoBuf.Grpc;
using RacerContracts;
using Wolverine.Http.Grpc;

namespace RacerServer;

/// <summary>
/// Bidirectional-streaming gRPC endpoint that runs the race.
///
/// Wolverine discovers this class automatically because:
///   - It is decorated with <c>[WolverineGrpcService]</c>
///   - It inherits <see cref="WolverineGrpcEndpointBase"/> (giving access to <c>Bus</c>
///     for any additional Wolverine message dispatch needed alongside the stream)
///
/// For every <see cref="RacerUpdate"/> the client sends, the server updates its
/// in-memory speed table, recomputes standings, and yields back the updated
/// <see cref="RacePosition"/> for that racer.
///
/// This demonstrates that high-performance bidirectional (duplex) streaming is possible
/// with Wolverine-hosted gRPC services, with no special framework changes needed —
/// <c>IAsyncEnumerable&lt;T&gt;</c> in / out is all that protobuf-net.Grpc requires.
/// </summary>
[WolverineGrpcService]
public class RacingService : WolverineGrpcEndpointBase, IRacingService
{
    // Track the latest reported speed per racer for this call.
    // Each gRPC call gets its own RacingService instance (transient/scoped), so this
    // dictionary is per-connection — not shared across clients.
    private readonly ConcurrentDictionary<string, double> _speeds = new();

    public async IAsyncEnumerable<RacePosition> RaceAsync(
        IAsyncEnumerable<RacerUpdate> updates,
        CallContext context = default)
    {
        await foreach (var update in updates.WithCancellation(context.CancellationToken))
        {
            // Record the latest speed for this racer.
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

            // Yield the updated position for the racer who just sent an update.
            var position = standings.FirstOrDefault(p => p.RacerId == update.RacerId);
            if (position is not null) yield return position;
        }
    }
}
