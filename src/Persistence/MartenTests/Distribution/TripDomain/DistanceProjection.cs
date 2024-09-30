using Marten.Events;
using Marten.Events.Projections;

namespace MartenTests.Distribution.TripDomain;

public class DistanceProjection : EventProjection
{
    public DistanceProjection()
    {
        ProjectionName = "Distance";
    }

    // Create a new Distance document based on a Travel event
    public Distance Create(Travel travel, IEvent e)
    {
        return new Distance { Id = e.Id, Day = travel.Day, Total = travel.TotalDistance() };
    }
}