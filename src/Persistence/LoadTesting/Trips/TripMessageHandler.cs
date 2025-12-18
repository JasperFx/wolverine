using JasperFx.Core;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;

namespace LoadTesting.Trips;

[AggregateHandler]
public static class TripMessageHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<TransientException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds())
            .Then.MoveToErrorQueue();

        chain.OnException<OtherTransientException>()
            .Requeue(3).Then.MoveToErrorQueue();
            
        chain.OnAnyException().MoveToErrorQueue();
    }

    public static void Before()
    {
        // Chaos monkey
        var next = Random.Shared.NextDouble();
        if (next < .01)
        {
            throw new TripServiceTooBusyException("Just feeling tired at " + DateTime.Now);
        }
        
        if (next < .02)
        {
            throw new TrackingUnavailableException("Tracking is down at " + DateTime.Now);
        }
        
        if (next < .03)
        {
            throw new DatabaseIsTiredException("The database wants a break at " + DateTime.Now);
        }
        
        if (next < .04)
        {
            throw new TransientException("Slow down, you move too fast.");
        }
        
        if (next < .05)
        {
            throw new OtherTransientException("Slow down, you move too fast.");
        }
    }
    
    public static Traveled Handle(RecordTravel message, Trip trip)
    {
        return message.Event;
    }

    public static TripAborted Handle(AbortTrip command, Trip trip) => new();

    public static (BrokeDown, OutgoingMessages) Handle(RecordBreakdown command, Trip trip)
    {
        var e = new BrokeDown(command.IsCritical);
        return command.IsCritical 
            ? (e, [new RepairRequested(command.TripId, trip.State)]) 
            : (e, []);
    }

    public static VacationOver Handle(MarkVacationOver command, Trip trip) => new();

    public static Arrival Handle(Arrive command, Trip trip) => new(command.Day, command.State);

    public static Departure Handle(Depart command, Trip trip) => new(command.Day, command.State);

    public static TripEnded Handle(EndTrip command, Trip trip) => new(command.Day, command.State);

    public static TripResumed Handle(RepairsCompleted e, Trip trip) => new();
}