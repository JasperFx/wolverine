namespace Wolverine.Tracking;

#region sample_record_collections
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
    MovedToErrorQueue,
    Requeued,
    Scheduled,
    Discarded,
    Status
}
#endregion