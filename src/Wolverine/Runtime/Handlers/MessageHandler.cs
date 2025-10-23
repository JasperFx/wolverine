using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Handlers;

#region sample_MessageHandler

public interface IMessageHandler
{
    Type MessageType { get; }

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

    public virtual Type MessageType => Chain!.MessageType;

    public LogLevel SuccessLogLevel => Chain!.SuccessLogLevel;
    public LogLevel ProcessingLogLevel => Chain!.ProcessingLogLevel;

    public bool TelemetryEnabled => Chain!.TelemetryEnabled;
}

#endregion

public abstract class MessageHandler<T> : MessageHandler
{
    public sealed override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        if (context.Envelope.Message is T message)
        {
            return HandleAsync(message, context, cancellation);
        }

        throw new ArgumentOutOfRangeException(nameof(context),
            $"Wrong message type {context.Envelope.Message.GetType().FullNameInCode()}, expected {typeof(T).FullNameInCode()}");
    }

    /// <summary>
    /// Template method hook to optionally configure error handling policies for this message
    /// handler
    /// </summary>
    /// <param name="chain"></param>
    public virtual void ConfigureChain(HandlerChain chain)
    {
        // Nothing
    }

    protected abstract Task HandleAsync(T message, MessageContext context, CancellationToken cancellation);

    public override Type MessageType => typeof(T);
}