using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.Partitioning;

internal class PartitionedMessageReRouter : IMessageHandler
{
    private readonly PartitionedMessageTopology[] _topologies;

    /// <summary>
    /// GH-5916. A message type routed through several sharded topologies, each running its own handler types, is
    /// re-routed to a slot of each of them
    /// </summary>
    public PartitionedMessageReRouter(PartitionedMessageTopology[] topologies, Type messageType)
    {
        _topologies = topologies;
        MessageType = messageType;
    }

    public Type MessageType { get; }

    public async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        // Knock it out of being scheduled just in case
        // From https://github.com/JasperFx/wolverine/issues/2026
        context.Envelope!.ClearAnyScheduling();

        foreach (var topology in _topologies)
        {
            var endpoint = topology.SelectSlot(context.Envelope);

            await context
                .EndpointFor(endpoint.Uri)
                .SendAsync(context.Envelope.Message, context.Envelope.ToDeliveryOptions());
        }
    }

    public LogLevel ExecutionLogLevel => LogLevel.Debug;
    public LogLevel SuccessLogLevel { get; } = LogLevel.Debug;
    public LogLevel ProcessingLogLevel { get; } = LogLevel.Debug;
    public bool TelemetryEnabled { get; } = true;
}