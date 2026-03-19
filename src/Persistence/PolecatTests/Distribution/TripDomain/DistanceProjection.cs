using JasperFx.Events;
using Polecat.Projections;

namespace PolecatTests.Distribution.TripDomain;

public class DistanceProjection : EventProjection
{
    public DistanceProjection()
    {
        Name = "Distance";
    }

    public Distance Create(Travel travel, IEvent e)
    {
        return new Distance { Id = e.Id, Day = travel.Day, Total = travel.TotalDistance() };
    }
}
