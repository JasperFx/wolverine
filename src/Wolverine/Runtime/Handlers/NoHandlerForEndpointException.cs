using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

public class NoHandlerForEndpointException : Exception
{
    public Type MessageType { get; }

    public NoHandlerForEndpointException(Type messageType) : base($"No handlers for message type {messageType.FullNameInCode()} at this endpoint. This is usually because of 'sticky' handler to endpoint configuration. See https://wolverinefx.net/guide/messaging/subscriptions.html")
    {
        MessageType = messageType;
    }

    public NoHandlerForEndpointException(Type messageType, Uri endpointUri) : base($"No handlers for message type {messageType.FullNameInCode()} at endpoint {endpointUri}. This is usually because of 'sticky' handler to endpoint configuration. See https://wolverinefx.net/guide/messaging/subscriptions.html")
    {
        MessageType = messageType;
        Uri = endpointUri;
    }

    // GH-5136. A message type with one handler chain per handler type has no single handler to run outside of
    // the endpoints those chains are bound to
    internal NoHandlerForEndpointException(Type messageType, MessageHandlerGroup group) : base(
        $"Message type {messageType.FullNameInCode()} is handled by {group.Chains.Count} handler types " +
        $"({group.DescribeHandlerTypes()}), each with its own handler chain and endpoint, so there is no single handler " +
        "to run it inline. Publish or send the message instead so that each handler receives it on its own endpoint.")
    {
        MessageType = messageType;
    }

    // GH-5137. Received at an endpoint none of the message type's chains is bound to, with nothing to relay it to
    internal NoHandlerForEndpointException(Type messageType, Uri endpointUri, MessageHandlerGroup group) : base(
        $"No handler for message type {messageType.FullNameInCode()} at endpoint {endpointUri}. It is handled by " +
        $"{group.DescribeBindings()}, and a sticky handler runs only at the endpoints it is sticky to. " +
        "See https://wolverinefx.net/guide/messaging/subscriptions.html")
    {
        MessageType = messageType;
        Uri = endpointUri;
    }

    public Uri? Uri { get; set; }
}