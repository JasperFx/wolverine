namespace PolecatTests.Distribution.TripDomain;

public interface IDayEvent
{
    int Day { get; set; }
}

public class TripStarted : IDayEvent
{
    public int Day { get; set; }
}

public class TripEnded : IDayEvent
{
    public int Day { get; set; }
}

public class Arrival
{
    public required string State { get; init; }
}

public class TripAborted;

public class Trip
{
    public Guid Id { get; set; }
    public int EndedOn { get; set; }
    public double Traveled { get; set; }
    public string State { get; set; } = string.Empty;
    public bool Active { get; set; }
    public int StartedOn { get; set; }
}

public class Travel : IDayEvent
{
    public int Day { get; set; }

    public double TotalDistance()
    {
        return Movements.Sum(x => x.Distance);
    }

    public IList<Movement> Movements { get; set; } = new List<Movement>();
}

public class Movement
{
    public Direction Direction { get; set; }
    public double Distance { get; set; }
}

public enum Direction
{
    North,
    South,
    East,
    West
}

public class Day
{
    public int Id { get; set; }
    public int Started { get; set; }
    public int Ended { get; set; }
    public double North { get; set; }
    public double South { get; set; }
    public double East { get; set; }
    public double West { get; set; }
}

public class Distance
{
    public Guid Id { get; set; }
    public double Total { get; set; }
    public int Day { get; set; }
}
