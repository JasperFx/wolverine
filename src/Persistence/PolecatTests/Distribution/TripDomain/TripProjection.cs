using Polecat.Projections;

namespace PolecatTests.Distribution.TripDomain;

public class TripProjection : SingleStreamProjection<Trip>
{
    public TripProjection()
    {
        Name = "Trip";

        DeleteEvent<TripAborted>();
    }

    public void Apply(Arrival e, Trip trip)
    {
        trip.State = e.State;
    }

    public void Apply(Travel e, Trip trip)
    {
        trip.Traveled += e.TotalDistance();
    }

    public void Apply(TripEnded e, Trip trip)
    {
        trip.Active = false;
        trip.EndedOn = e.Day;
    }

    public Trip Create(TripStarted started)
    {
        return new Trip { StartedOn = started.Day, Active = true };
    }
}
