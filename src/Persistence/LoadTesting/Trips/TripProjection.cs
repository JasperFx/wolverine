using JasperFx.Events;
using Marten;
using Marten.Events.Aggregation;

namespace LoadTesting.Trips;

public class TripProjection: SingleStreamProjection<Trip, Guid>
{
    // These methods can be either public, internal, or private but there's
    // a small performance gain to making them public
    public void Apply(Arrival e, Trip trip) => trip.State = e.State;
    public void Apply(Traveled e, Trip trip) => trip.Traveled += e.TotalDistance();

    public void Apply(Departure e, Trip trip)
    {
        trip.Active = true;
        trip.WaitingRepairs = false;
        trip.State = e.State;
    }
    
    public void Apply(TripEnded e, Trip trip)
    {
        trip.Active = false;
        trip.EndedOn = e.Day;
        trip.State = e.State;
    }

    public Trip Create(TripStarted started)
    {
        return new Trip { StartedOn = started.Day, Active = true, State = started.State};
    }

    public void Apply(BrokeDown e, Trip trip)
    {
        trip.WaitingRepairs = true;
    }

    public void Apply(TripResumed e, Trip trip) => trip.WaitingRepairs = false;
    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<Trip> slice)
    {
        if (slice.Snapshot is { WaitingRepairs: false })
        {
            slice.PublishMessage(new ContinueTrip(slice.Snapshot.Id));
        }

        return new ValueTask();
    }
}