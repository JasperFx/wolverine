using JasperFx.Core.Reflection;

namespace Wolverine.Tracking;

internal class WaitForMessage<T> : ITrackedCondition
{
    private bool _isCompleted;

    public Guid UniqueNodeId { get; set; }

    public void Record(EnvelopeRecord record)
    {
        // GH-4125/4136: MovedToErrorQueue counts. This condition means "wait until this message
        // reaches a terminal outcome at that host", and being dead-lettered is one -- nothing further
        // will ever happen to the envelope. Leaving it out meant a WaitForMessageToBeReceivedAt over a
        // message that fails into the DLQ could never be satisfied, so the session ran to its timeout
        // every time. That was invisible while the timeout assertion was suppressed.
        if (record.MessageEventType != MessageEventType.MessageSucceeded &&
            record.MessageEventType != MessageEventType.MessageFailed &&
            record.MessageEventType != MessageEventType.MovedToErrorQueue)
        {
            return;
        }

        if (record.Envelope!.Message is T)
        {
            if (UniqueNodeId != Guid.Empty && UniqueNodeId != record.UniqueNodeId)
            {
                return;
            }

            _isCompleted = true;
        }
    }

    public bool IsCompleted()
    {
        return _isCompleted;
    }

    public override string ToString()
    {
        var description = $"Wait for message of type {typeof(T).FullNameInCode()} to be received";
        if (UniqueNodeId != Guid.Empty)
        {
            description += " at node " + UniqueNodeId;
        }

        return description;
    }
}