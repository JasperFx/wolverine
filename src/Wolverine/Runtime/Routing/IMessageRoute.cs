using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

#region sample_IMessageRoute

/// <summary>
/// Contains all the rules for where and how an outgoing message
/// should be sent to a single subscriber
/// </summary>
public interface IMessageRoute
{
    Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName);

    MessageSubscriptionDescriptor Describe();
}

#endregion

/// <summary>
/// Diagnostic view of a subscription
/// </summary>
public class MessageSubscriptionDescriptor
{
    public Uri Endpoint { get; init; } = new Uri("null://null");
    public string ContentType { get; set; } = "application/json";
    public string Description { get; set; } = string.Empty;

    // TODO -- add something about envelope rules?
}

internal class TransformedMessageRouteSource : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        var transformations = runtime.Options.MessageTransformations.Where(x => x.SourceType == messageType);
        return transformations.SelectMany(t => runtime.RoutingFor(t.DestinationType).Routes.Select(t.CreateRoute)).ToArray();
    }

    public bool IsAdditive => true;
}