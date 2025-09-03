namespace Wolverine.SignalR;

/// <summary>
/// Marker interface for any side effect that deals with SignalR actions
/// </summary>
public interface ISignalRAction : ISideEffect
{
    ValueTask ExecuteAsync(IMessageContext context, CancellationToken cancellation);
}