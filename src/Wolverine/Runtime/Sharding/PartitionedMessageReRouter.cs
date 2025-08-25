using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.Sharding;

internal class PartitionedMessageReRouter : IMessageHandler
{
    private readonly PartitionedMessageTopology _topology;

    public PartitionedMessageReRouter(PartitionedMessageTopology topology, Type messageType)
    {
        _topology = topology;
        MessageType = messageType;
    }

    public Type MessageType { get; }

    public Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var endpoint = _topology.SelectSlot(context.Envelope);

        return context
            .EndpointFor(endpoint.Uri)
            .SendAsync(context.Envelope.Message, context.Envelope.ToDeliveryOptions()).AsTask();
    }

    public LogLevel ExecutionLogLevel => LogLevel.Debug;
    public LogLevel SuccessLogLevel { get; } = LogLevel.Debug;
    public LogLevel ProcessingLogLevel { get; } = LogLevel.Debug;
    public bool TelemetryEnabled { get; } = true;
}