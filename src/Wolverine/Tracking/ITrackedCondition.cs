namespace Wolverine.Tracking;

public interface ITrackedCondition
{
    void Record(EnvelopeRecord record);
    bool IsCompleted();
}
