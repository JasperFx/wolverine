using JasperFx.Core;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi;

public record StartReservation(string ReservationId);

public record ReservationBooked(string ReservationId, DateTimeOffset Time);

public static class ReservationEndpoint
{
    #region sample_starting_saga_from_http_endpoint

    [WolverinePost("/reservation")]
    public static (
        // The first return value would be written out as the HTTP response body
        ReservationBooked, 
        
        // Because this subclasses from Saga, Wolverine will persist this entity
        // with saga persistence
        Reservation, 
        
        // Other return values that trigger no special handling will be treated
        // as cascading messages
        ReservationTimeout) Post(StartReservation start)
    {
        return (new ReservationBooked(start.ReservationId, DateTimeOffset.UtcNow), new Reservation { Id = start.ReservationId }, new ReservationTimeout(start.ReservationId));
    }

    #endregion
}

public record BookReservation(string Id);

public record ReservationTimeout(string Id) : TimeoutMessage(1.Minutes());

#region sample_return_saga_from_handler

public class StartReservationHandler
{
    public static (
        // Outgoing message
        ReservationBooked, 
        
        // Starts a new Saga
        Reservation, 
        
        // Additional message cascading for the new saga
        ReservationTimeout) Handle(StartReservation start)
    {
        return (
            new ReservationBooked(start.ReservationId, DateTimeOffset.UtcNow), 
            new Reservation { Id = start.ReservationId }, 
            new ReservationTimeout(start.ReservationId)
            );
    }
}
#endregion

#region sample_reservation_saga

public class Reservation : Saga
{
    public string? Id { get; set; }
    
    // Apply the CompleteReservation to the saga
    public void Handle(BookReservation book, ILogger<Reservation> logger)
    {
        logger.LogInformation("Completing Reservation {Id}", book.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }

    // Delete this Reservation if it has not already been deleted to enforce a "timeout"
    // condition
    public void Handle(ReservationTimeout timeout, ILogger<Reservation> logger)
    {
        logger.LogInformation("Applying timeout to Reservation {Id}", timeout.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }
}

#endregion