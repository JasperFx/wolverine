using JasperFx.Core.Reflection;

namespace Wolverine.Tracking;

internal class WaitForMessage<T> : ITrackedCondition
{
    private bool _isCompleted;

    public Guid UniqueNodeId { get; set; }

    public void Record(EnvelopeRecord record)
    {
        // GH-4125: deliberately NOT satisfied by MovedToErrorQueue. A dead letter is not the end of
        // the story -- it can be marked replayable and redelivered, and a test waiting on this message
        // is usually waiting for exactly that second, successful pass (see
        // MartenTests.Bugs.Bug_971_replay_dead_letter_queue_of_event_wrapper). Completing on the
        // dead-letter would return the session on the FAILING delivery and skip the replay entirely.
        if (record.MessageEventType != MessageEventType.MessageSucceeded &&
            record.MessageEventType != MessageEventType.MessageFailed)
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