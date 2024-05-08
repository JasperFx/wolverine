using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Handlers;

#region sample_MessageHandler

public interface IMessageHandler
{
    Type MessageType { get; }

    [Obsolete("This name was misleading, use SuccessLogLevel instead")]
    LogLevel ExecutionLogLevel { get; }

    LogLevel SuccessLogLevel { get; }

    LogLevel ProcessingLogLevel { get; }

    /// <summary>
    /// Is OpenTelemetry logging enabled for invoking this message type?
    /// </summary>
    bool TelemetryEnabled { get; }

    Task HandleAsync(MessageContext context, CancellationToken cancellation);
}

public abstract class MessageHandler : IMessageHandler
{
    public HandlerChain? Chain { get; set; }

    public abstract Task HandleAsync(MessageContext context, CancellationToken cancellation);

    public Type MessageType => Chain!.MessageType;

    public LogLevel ExecutionLogLevel => Chain!.ExecutionLogLevel;

    public LogLevel SuccessLogLevel => Chain!.SuccessLogLevel;
    public LogLevel ProcessingLogLevel => Chain!.ProcessingLogLevel;

    public bool TelemetryEnabled => Chain!.TelemetryEnabled;
}

#endregion