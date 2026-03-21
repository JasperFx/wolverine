namespace MartenTests.Distribution.TripDomain;

public class Stop
{
    public TimeOnly Time { get; set; }
    public string State { get; set; } = null!;
    public int Duration { get; set; }
}