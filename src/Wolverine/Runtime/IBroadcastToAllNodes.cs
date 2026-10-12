namespace Wolverine.Runtime;

/// <summary>
///     The implementation behind <see cref="MessageBusExtensions.BroadcastToAllNodesAsync{T}"/>. An
///     internal interface rather than a member of <see cref="IMessageBus"/> so that adding the
///     operation does not break any other implementation of that public interface.
/// </summary>
internal interface IBroadcastToAllNodes
{
    ValueTask BroadcastToAllNodesAsync(object message, DeliveryOptions? options);
}
