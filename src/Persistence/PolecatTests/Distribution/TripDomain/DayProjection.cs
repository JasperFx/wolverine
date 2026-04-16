using Polecat.Projections;

namespace PolecatTests.Distribution.TripDomain;

public class DayProjection : MultiStreamProjection<Day, int>
{
    public DayProjection()
    {
        Identity<IDayEvent>(x => x.Day);

        FanOut<Travel, Movement>(x => x.Movements);

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
}
