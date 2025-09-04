namespace Wolverine.SignalR;

/// <summary>
/// Marker interface for types that are predominantly
/// sent or received through SignalR. Merely tells Wolverine
/// to use a JS friendly message type name for these message types
/// </summary>
public interface WebSocketMessage : IMessage;