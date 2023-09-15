using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.Agents;

internal class InternalMessageHandler<T> : IMessageHandler
{
    private readonly IInternalHandler<T> _handler;

    public InternalMessageHandler(IInternalHandler<T> handler)
    {
        _handler = handler;
    }

    public async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var message = (T)context.Envelope!.Message!;

        await foreach (var outgoing in _handler.HandleAsync(message).WithCancellation(cancellation))
            await context.EnqueueCascadingAsync(outgoing);
    }

    public Type MessageType => typeof(T);
    public LogLevel ExecutionLogLevel => LogLevel.None;

    public LogLevel SuccessLogLevel => LogLevel.Debug;
    public LogLevel ProcessingLogLevel => LogLevel.None;

    public bool TelemetryEnabled => false;
}

internal interface IInternalHandler<T>
{
    IAsyncEnumerable<object> HandleAsync(T message);
}