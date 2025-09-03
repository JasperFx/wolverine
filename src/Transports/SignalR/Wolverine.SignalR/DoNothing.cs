namespace Wolverine.SignalR;

public record DoNothing : ISignalRAction
{
    public ValueTask ExecuteAsync(IMessageContext context, CancellationToken cancellation)
    {
        return new ValueTask();
    }
}