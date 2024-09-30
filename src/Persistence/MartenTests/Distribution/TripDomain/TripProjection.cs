using Marten.Events.Aggregation;

namespace MartenTests.Distribution.TripDomain;

public class TripProjection : SingleStreamProjection<Trip>
{
    public TripProjection()
    {
        ProjectionName = "Trip";

        DeleteEvent<TripAborted>();
    }

    public bool ShouldDelete(Breakdown b)
    {
        return b.IsCritical;
    }

    public bool ShouldDelete(VacationOver e, Trip trip)
    {
        return trip.Traveled > 1000;
    }

    // These methods can be either public, internal, or private but there's
    // a small performance gain to making them public
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

public class Trip2Projection : SingleStreamProjection<Trip>
{
    public Trip2Projection()
    {
        ProjectionName = "Trip";
        ProjectionVersion = 2;

        DeleteEvent<TripAborted>();
    }

    public bool ShouldDelete(Breakdown b)
    {
        return b.IsCritical;
    }

    public bool ShouldDelete(VacationOver e, Trip trip)
    {
        return trip.Traveled > 1000;
    }

    // These methods can be either public, internal, or private but there's
    // a small performance gain to making them public
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