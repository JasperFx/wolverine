using JasperFx.Events;
using Marten.Events.Projections;

namespace LoadTesting.Trips;

public class Day
{
    public long Version { get; set; }

    public int Id { get; set; }

    // how many trips started on this day?
    public int Started { get; set; }

    // how many trips ended on this day?
    public int Ended { get; set; }

    public int Stops { get; set; }

    // how many miles did the active trips
    // drive in which direction on this day?
    public double North { get; set; }
    public double East { get; set; }
    public double West { get; set; }
    public double South { get; set; }
}

public class DayProjection: MultiStreamProjection<Day, int>
{
    public DayProjection()
    {
        // Tell the projection how to group the events
        // by Day document
        Identity<IDayEvent>(x => x.Day);

        // This just lets the projection work independently
        // on each Movement child of the Travel event
        // as if it were its own event
        FanOut<Traveled, Movement>(x => x.Movements);

        // You can also access Event data
        FanOut<Traveled, Stop>(x => x.Data.Stops);

        ProjectionName = "Day";

        // Opt into 2nd level caching of up to 100
        // most recently encountered aggregates as a
        // performance optimization
        CacheLimitPerTenant = 1000;

        // With large event stores of relatively small
        // event objects, moving this number up from the
        // default can greatly improve throughput and especially
        // improve projection rebuild times
        Options.BatchSize = 5000;
    }

    public void Apply(Day day, TripStarted e) => day.Started++;
    public void Apply(Day day, TripEnded e) => day.Ended++;

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

    public void Apply(Day day, Stop e) => day.Stops++;
}

public class Distance
{
    public Guid Id { get; set; }
    
    public double Total { get; set; }
    
    public int Day { get; set; }
}

public class DistanceProjection: EventProjection
{
    public DistanceProjection()
    {
        ProjectionName = "Distance";
    }

    // Create a new Distance document based on a Travel event
    public Distance Create(Traveled travel, IEvent e)
    {
        return new Distance {Id = e.Id, Day = travel.Day, Total = travel.TotalDistance()};
    }
}
