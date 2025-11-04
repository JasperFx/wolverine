using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports;

namespace Wolverine.Runtime.Stubs;

public class StubMessageHandler<T> : IMessageHandler
{
    private readonly IServiceProvider _services;

    public StubMessageHandler(Func<T, IMessageContext, IServiceProvider, CancellationToken, Task> func, IServiceProvider services)
    {
        Func = func;
        _services = services;
    }

    // TODO -- override this!
    public Func<T, IMessageContext, IServiceProvider, CancellationToken, Task> Func { get; internal set; }

    public Task HandleAsync(T message, IMessageContext context, IServiceProvider services, CancellationToken cancellation)
    {
        return Func(message, context, services, cancellation);
    }
    
    public Type MessageType => typeof(T);
    public LogLevel SuccessLogLevel => LogLevel.None;
    public LogLevel ProcessingLogLevel => LogLevel.None;
    public bool TelemetryEnabled => false;
    public Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var message = (T)context.Envelope.Message;
        return Func(message, context, _services, cancellation);
    }
}