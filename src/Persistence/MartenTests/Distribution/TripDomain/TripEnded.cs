namespace MartenTests.Distribution.TripDomain;

public class TripEnded : IDayEvent
{
    public string State { get; set; }
    public int Day { get; set; }
}