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

    public Uri? Uri { get; set; }
}