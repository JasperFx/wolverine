namespace Wolverine.Tracking;

internal interface ITrackedCondition
{
    void Record(EnvelopeRecord record);
    bool IsCompleted();
}
