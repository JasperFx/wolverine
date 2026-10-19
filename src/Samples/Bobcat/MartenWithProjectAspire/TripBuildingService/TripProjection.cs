using Marten.Events.Aggregation;
using TripDomain;

public class TripProjection: SingleStreamProjection<Trip, Guid>
{
    // Marten 9 replaced the predicate overloads of DeleteEvent<T>() with the ShouldDelete
    // convention. The parameterless DeleteEvent<T>() still exists, but expressing all three
    // the same way keeps the rule for each event in one place.
    public bool ShouldDelete(TripAborted _) => true;
    public bool ShouldDelete(Breakdown e) => e.IsCritical;
    public bool ShouldDelete(VacationOver _, Trip trip) => trip.Traveled > 1000;

    // These methods can be either public, internal, or private but there's
    // a small performance gain to making them public
    public void Apply(Arrival e, Trip trip) => trip.State = e.State;
    public void Apply(Travel e, Trip trip) => trip.Traveled += e.TotalDistance();

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