using Marten.Events.Projections;
using Marten.Schema;

namespace MartenTests.Distribution.TripDomain;

public class Ending
{
    public long Count { get; set; }

    [Identity] public int Day { get; set; }
}

public class EndingProjection : MultiStreamProjection<Ending, int>
{
    public EndingProjection()
    {
        // Tell the projection how to group the events
        // by Day document
        Identity<IDayEvent>(x => x.Day);
    }

    public void Apply(TripEnded ended, Ending ending)
    {
        ending.Count++;
    }
}

public class Starting
{
    public long Count { get; set; }

    [Identity] public int Day { get; set; }
}

public class StartingProjection : MultiStreamProjection<Starting, int>
{
    public StartingProjection()
    {
        // Tell the projection how to group the events
        // by Day document
        Identity<IDayEvent>(x => x.Day);
    }

    public void Apply(TripEnded ended, Starting starting)
    {
        starting.Count++;
    }
}

public class DayProjection : MultiStreamProjection<Day, int>
{
    public DayProjection()
    {
        // Tell the projection how to group the events
        // by Day document
        Identity<IDayEvent>(x => x.Day);

        // This just lets the projection work independently
        // on each Movement child of the Travel event
        // as if it were its own event
        FanOut<Travel, Movement>(x => x.Movements);

        // You can also access Event data
        FanOut<Travel, Stop>(x => x.Data.Stops);

        Name = "Day";
    }

    public void Apply(Day day, TripStarted e)
    {
        day.Started++;
    }

    public void Apply(Day day, TripEnded e)
    {
        day.Ended++;
    }

    public void Apply(Day day, Movement e)
    {
        switch (e.Direction)
        {
            case Direction.East:
                day.East += e.Distance;
                break;
            case Direction.North:
                day.North += e.Distance;
                break;
            case Direction.South:
                day.South += e.Distance;
                break;
            case Direction.West:
                day.West += e.Distance;
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public void Apply(Day day, Stop e)
    {
        day.Stops++;
    }
}