namespace Wolverine.Tracking;

public enum MessageEventType
{
    Received,
    Sent,
    ExecutionStarted,
    ExecutionFinished,
    MessageSucceeded,
    MessageFailed,
    NoHandlers,
    NoRoutes,
    MovedToErrorQueue
}