using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime;

/// <summary>
/// Endpoint-originated causation reporting (CritterWatch #396 Phase 4 item 5). HTTP/gRPC
/// endpoints are not <see cref="Wolverine.Runtime.Handlers.MessageHandler"/> subclasses, so
/// <c>MessageHandler.RecordCauseAndEffect</c> never runs for them and messages published from an
/// endpoint have no observed cause (there is no incoming message — <c>RecordCauseAndEffect</c>
/// keys on the incoming message type). This attributes each published message to the endpoint
/// origin (the route+verb or service.method) instead, so CritterWatch's observed-causation graph
/// (and the lifecycle assembler's observed edges) can see endpoint-originated publishes.
///
/// Injected into HTTP/gRPC endpoint codegen by <see cref="RecordEndpointCausationFrame"/>.
/// </summary>
public static class EndpointCausation
{
    public static void RecordEndpointCauseAndEffect(
        MessageContext context, IWolverineObserver observer, string endpointOrigin, string? handlerType)
    {
        foreach (var envelope in context.Outstanding)
        {
            var outgoingMessage = envelope.Message;
            if (outgoingMessage is null) continue;

            var outgoingMessageType = outgoingMessage.GetType();
            if (outgoingMessageType.IsSystemMessageType()) continue;

            var outgoingType = outgoingMessageType.FullName;
            if (string.IsNullOrEmpty(outgoingType)) continue;

            // endpointOrigin (e.g. "POST /orders" or "Orders.Ship") stands in for the incoming
            // message type; the lifecycle assembler renders it on the trigger lane.
            observer.MessageCausedBy(endpointOrigin, outgoingType!, handlerType ?? endpointOrigin, null);
        }
    }
}
