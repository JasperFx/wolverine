namespace Wolverine.Transports;

/// <summary>
/// Controls how conventional routing determines the name of queues, topics, and other
/// transport-specific identifiers for message listeners
/// </summary>
public enum NamingSource
{
    /// <summary>
    /// Default behavior. Names queues/topics after the message type name. All handlers
    /// for the same message type share a single listener endpoint.
    /// </summary>
    FromMessageType,

    /// <summary>
    /// Names queues/topics after the handler type name. Each handler for a message type
    /// gets its own dedicated listener endpoint. This is appropriate for modular monolith
    /// scenarios where you have more than one handler for a given message type and want
    /// each handler to receive messages independently.
    /// </summary>
    FromHandlerType
}
