namespace MartenTests.Distribution.TripDomain;

public class TripEnded : IDayEvent
{
    public string State { get; set; } = null!;
    public int Day { get; set; }
}