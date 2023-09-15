using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.RemoteInvocation;

internal class FailureAcknowledgementHandler : IMessageHandler
{
    private readonly IReplyTracker _replies;
    private readonly ILogger _logger;

    public FailureAcknowledgementHandler(IReplyTracker replies, ILogger logger)
    {
        _replies = replies;
        _logger = logger;
    }

    public Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var ack = context.Envelope.Message as FailureAcknowledgement;
        _logger.LogError("Received failure acknowledgement on reply for message {Id} from service {Service} with message '{Message}'", context.Envelope.ConversationId, context.Envelope.Source ?? "Unknown", ack?.Message);   
        _replies.Complete(context.Envelope!);
        return Task.CompletedTask;
    }

    public Type MessageType => typeof(FailureAcknowledgement);
    public LogLevel ExecutionLogLevel => LogLevel.None;

    public LogLevel SuccessLogLevel => LogLevel.None;
    public LogLevel ProcessingLogLevel => LogLevel.None;

    public bool TelemetryEnabled => false;
}