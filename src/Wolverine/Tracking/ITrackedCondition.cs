namespace Wolverine.Tracking;

/// <summary>
/// Interface for custom "wait" conditions for message tracking
/// test sessions. This is probably an advanced usage.
/// </summary>
public interface ITrackedCondition
{
    void Record(EnvelopeRecord record);
    bool IsCompleted();
}