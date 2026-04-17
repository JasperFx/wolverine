using ProtoBuf.Grpc;
using RacerContracts;
using Wolverine;
using Wolverine.Http.Grpc;

namespace RacerServer;

/// <summary>
///     Bidirectional-streaming gRPC endpoint. The service itself does the client-stream-to-bus
///     bridging: for each <see cref="RacerUpdate"/> the client sends, it invokes
///     <see cref="IMessageBus.StreamAsync{T}"/> and re-yields every <see cref="RacePosition"/>
///     the Wolverine handler produces. This lets bidirectional streaming "just work" in the
///     code-first style without requiring Wolverine itself to accept an inbound
///     <c>IAsyncEnumerable&lt;TRequest&gt;</c>.
/// </summary>
[WolverineGrpcService]
public class RacingGrpcService(IMessageBus bus) : IRacingService
{
    public async IAsyncEnumerable<RacePosition> RaceAsync(
        IAsyncEnumerable<RacerUpdate> updates,
        CallContext context = default)
    {
        await foreach (var update in updates.WithCancellation(context.CancellationToken))
        {
            await foreach (var position in bus.StreamAsync<RacePosition>(update, context.CancellationToken))
            {
                yield return position;
            }
        }
    }
}
