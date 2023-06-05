using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Handlers;

#region sample_MessageHandler

public interface IMessageHandler
{
    Type MessageType { get; }

    LogLevel ExecutionLogLevel { get; }
    Task HandleAsync(MessageContext context, CancellationToken cancellation);
}

public abstract class MessageHandler : IMessageHandler
{
    public HandlerChain? Chain { get; set; }

    public abstract Task HandleAsync(MessageContext context, CancellationToken cancellation);

    public Type MessageType => Chain!.MessageType;

    public LogLevel ExecutionLogLevel => Chain!.ExecutionLogLevel;
}

#endregion