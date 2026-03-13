using ProtoBuf.Grpc;
using RacerContracts;
using Wolverine;
using Wolverine.Http.Grpc;

namespace RacerServer;

/// <summary>
/// Bidirectional-streaming gRPC endpoint demonstrating IMessageBus.StreamAsync integration.
/// This version delegates to a Wolverine streaming handler (RaceStreamHandler) through the
/// message bus, enabling streaming through the full Wolverine middleware pipeline with
/// automatic OpenTelemetry instrumentation.
/// Uses attribute-based discovery with [WolverineGrpcService] and constructor injection.
/// </summary>
[WolverineGrpcService]
public class RacingGrpcService(IMessageBus bus) : IRacingService
{
    public async IAsyncEnumerable<RacePosition> RaceAsync(
        IAsyncEnumerable<RacerUpdate> updates,
        CallContext context = default)
    {
        // For each incoming update from the client, invoke the Wolverine streaming handler
        // through Bus.StreamAsync. This demonstrates:
        // 1. Streaming handler integration with IMessageBus
        // 2. OpenTelemetry instrumentation for each streamed message
        // 3. Middleware pipeline execution (cascading, side effects, etc.)
        await foreach (var update in updates.WithCancellation(context.CancellationToken))
        {
            // Stream results from the Wolverine handler through the message bus
            await foreach (var position in bus.StreamAsync<RacePosition>(update, context.CancellationToken))
            {
                yield return position;
            }
        }
    }
}
